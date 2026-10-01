using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace BoatMod
{
    internal static class WheelInput
    {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _deviceName;
        private static ConfigEntry<string> _steerCtl, _throttleCtl, _brakeCtl;
        private static ConfigEntry<bool> _steerInv, _throttleInv, _brakeInv;
        private static ConfigEntry<float> _steerDeadzone, _pedalDeadzone, _smoothing;
        private static ConfigEntry<float> _steerSmooth, _centerSnap, _steerCurve;
        private static ConfigEntry<bool> _directRudder;
        private static ConfigEntry<string> _calKey;
        private static ConfigEntry<string> _boostLearnKey;

        private static ConfigEntry<bool> _calSteerSaved;
        private static ConfigEntry<float> _calSteerCenter, _calSteerLeft, _calSteerRight;
        private static ConfigEntry<float> _calSteerRawLeft, _calSteerRawSpan, _calSteerTurnSign;

        private static Harmony _harmony;
        private static readonly Dictionary<Type, FieldInfo> _motorInputFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> _inputEnabledFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> _motorAngleFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> _motorFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, MethodInfo> _motorSetAngle = new Dictionary<Type, MethodInfo>();

        private static InputDevice _device;
        private static AxisControl _steer, _throttle, _brake;
        private static float _nextDeviceScan;

        private static float _restThrottle, _restBrake;
        private static float _captureUntil;
        private static int _captureSamples;
        private static bool _captured;
        private static float _throttleRange = 0.3f, _brakeRange = 0.3f;

        private static int _calStep;
        private static float _calStartedAt;
        private static float _calSum;
        private static int _calSamples;
        private static float _calLiveAt;
        private static float _wipCenter, _wipLeft, _wipRight;
        private static string _calMsg;
        private static float _calMsgUntil;

        private static void CalFeedback(string msg)
        {
            _calMsg = msg;
            _calMsgUntil = Time.unscaledTime + 8f;
        }

        private static GameObject _guiGO;
        private static float _nextGuiRecreate;
        private static bool _guiLoggedAlive;

        private static readonly float[] _ring = new float[64];
        private static int _ringIdx, _ringCount;

        private static float _lastRawSteer;
        private static float _unwrappedSteer;
        private static bool _unwrapBaselinePending;
        private static float _unwrapBaselineRaw;
        private static float _prevU;
        private static float _prevMappedRaw;
        private static bool _prevMappedValid;
        private static bool _reanchorArmed;
        private static float _pinTime;
        private static float _pinDirSum;
        private static bool _touchedOnce;

        private static void TrackUnwrap(float raw)
        {
            float delta = raw - _lastRawSteer;
            if (delta > 1f) delta -= 2f;
            else if (delta < -1f) delta += 2f;
            _lastRawSteer = raw;
            _unwrappedSteer += delta;
        }

        private static void ResetUnwrapBaseline(float raw)
        {
            _lastRawSteer = raw;
            _unwrapBaselineRaw = raw;
            _unwrappedSteer = 0f;
            _unwrapBaselinePending = false;
        }

        private static ConfigEntry<bool> _diag;
        private static ConfigEntry<string> _guiKey;
        private static bool _guiVisible;

        private static ConfigEntry<string> _fsKey;
        private static ConfigEntry<bool> _fsStart;
        private static int _winW = -1, _winH = -1;

        private static float _smSteer, _smThrottle, _smBrake;
        private static float _lastFrame;
        private static float _outSteer, _outPedal;
        private static bool _injectOn;
        private static float _nextDiagLog;
        private static bool _tickLogged;
        private static int _lastPumpedFrame = -1;
        private static bool _calLoggedStep;

        private static ConfigEntry<string> _boostButtons;
        private static readonly List<ButtonControl> _wheelButtons = new List<ButtonControl>();
        private static readonly List<string> _wheelButtonNames = new List<string>();
        private static readonly List<string> _wheelButtonPaths = new List<string>();
        private static readonly List<bool> _wheelButtonLast = new List<bool>();
        private static readonly List<ButtonControl> _boostCtl = new List<ButtonControl>();
        private static readonly List<string> _boostNames = new List<string>();
        private static readonly List<string> _learnPending = new List<string>();
        private static readonly Dictionary<Type, MethodInfo> _boostMethods = new Dictionary<Type, MethodInfo>();
        private static object _lastHandler;
        private static object _spaceHandler;
        private static float _boostLastFire;

        private static ManualLogSource Log => BoatModPlugin.Log;

        internal static void BindAndInit(ConfigFile cfg, Harmony harmony)
        {
            BindConfig(cfg);
            if (_calSteerSaved.Value && !PersistentCalUsable() && Math.Abs(_calSteerCenter.Value) > 0.35f)
                Log.LogWarning($"[BoatMod] wheel: old-format calibration has implausible center {_calSteerCenter.Value:F3} (>0.35) - press {_calKey.Value} once to regenerate persistent calibration");
            if (_calSteerSaved.Value && PersistentCalUsable())
                Log.LogInfo($"[BoatMod] wheel: persistent calibration loaded: left@raw {_calSteerRawLeft.Value:F3} span {_calSteerRawSpan.Value:F2} sign {_calSteerTurnSign.Value:F0}");
            _harmony = harmony;
            int patched = 0;
            foreach (var name in new[] { "BoatInputActionsHandler", "BoatInputHandler" })
            {
                var t = FindType(name);
                if (t == null) continue;
                var m = t.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null || m.DeclaringType != t) continue;
                try
                {
                    _harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(WheelInput), nameof(MotorInputPrefix)),
                        postfix: new HarmonyMethod(typeof(WheelInput), nameof(MotorInputPostfix)));
                    BoatModPlugin.Log.LogInfo($"[BoatMod] wheel: hooked {name}.FixedUpdate (prefix merge + postfix direct-rudder gated)");
                    patched++;
                    var sc = t.GetMethod("OnSuperchargerUsed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(InputAction.CallbackContext) }, null);
                    if (sc != null && sc.DeclaringType == t)
                    {
                        _harmony.Patch(sc, postfix: new HarmonyMethod(typeof(WheelInput), nameof(SuperchargeInputPostfix)));
                        BoatModPlugin.Log.LogInfo($"[BoatMod] wheel: hooked {name}.OnSuperchargerUsed(InputAction) - wheel boost targets whatever instance handles real Space presses");
                    }
                }
                catch (Exception e)
                {
                    BoatModPlugin.Log.LogWarning($"[BoatMod] wheel hook failed on {name}: {e.Message}");
                }
            }
            if (patched == 0) BoatModPlugin.Log.LogWarning("[BoatMod] wheel: no input handler types found yet");
            try
            {
                Application.onBeforeRender += OnBeforeRenderPump;
                BoatModPlugin.Log.LogInfo("[BoatMod] wheel: onBeforeRender pump installed (works in menus)");
            }
            catch (Exception e)
            {
                BoatModPlugin.Log.LogWarning($"[BoatMod] wheel: onBeforeRender pump subscribe failed: {e.Message}");
            }
        }

        private static void EnsureGui()
        {
            bool alive = _guiGO != null && _guiGO.activeSelf;
            if (!alive)
            {
                float now = Time.unscaledTime;
                if (_nextGuiRecreate > now) return;
                _nextGuiRecreate = now + 2f;
                try
                {
                    _guiGO = new GameObject("BoatModWheelGui");
                    UnityEngine.Object.DontDestroyOnLoad(_guiGO);
                    _guiGO.AddComponent<WheelGui>();
                    Log.LogInfo(_guiLoggedAlive ? "[BoatMod] wheel: debug overlay recreated (game had destroyed it)" : $"[BoatMod] wheel: debug overlay ready (toggle {_guiKey.Value}, auto-opens during calibration)");
                    _guiLoggedAlive = true;
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[BoatMod] wheel: debug overlay setup failed: {e.Message}");
                }
            }
        }

        private static void OnBeforeRenderPump()
        {
            PumpFromHook();
        }

        internal static void PumpFromHook()
        {
            int frame = Time.frameCount;
            if (frame == _lastPumpedFrame) return;
            _lastPumpedFrame = frame;
            Tick();
        }

        internal static void Tick()
        {
            if (KeyDownNow(ParseNamedKey(_guiKey)))
            {
                _guiVisible = !_guiVisible;
                Log.LogInfo($"[BoatMod] wheel: debug overlay toggled {_guiVisible}");
            }
            if (KeyDownNow(ParseNamedKey(_boostLearnKey)) && _boostButtons != null)
            {
                _boostButtons.Value = "";
                _cfg?.Save();
                _boostCtl.Clear();
                _boostNames.Clear();
                _learnPending.Clear();
                CalFeedback("BOOST rebind: press the two wheel buttons now (first two distinct presses are saved)");
                Log.LogInfo("[BoatMod] wheel: boost binding CLEARED - learn mode armed, press two distinct wheel buttons");
            }
            if (KeyDownNow(ParseNamedKey(_fsKey)))
                ToggleFullscreen();
            if (!_tickLogged)
            {
                _tickLogged = true;
                int devCount = -1;
                try { devCount = InputSystem.devices.Count; } catch (Exception e) { Log.LogWarning($"[BoatMod] wheel: devices probe failed: {e.Message}"); }
                Log.LogInfo($"[BoatMod] wheel: Tick alive, enabled={_enabled?.Value.ToString() ?? "null"}, InputSystem.devices={devCount}");
                try
                {
                    if (Screen.fullScreenMode != FullScreenMode.FullScreenWindow)
                    {
                        _winW = Screen.width;
                        _winH = Screen.height;
                        if (_fsStart != null && _fsStart.Value) ApplyFullscreen(true);
                    }
                }
                catch (Exception e) { Log.LogWarning($"[BoatMod] display: startup mode failed: {e.Message}"); }
            }
            if (_enabled?.Value != true)
            {
                _injectOn = false;
                _device = null;
                _captured = false;
                _calStep = 0;
                return;
            }

            EnsureGui();
            EnsureUiText();
            UpdateUiText();

            if (_device == null)
            {
                if (Time.unscaledTime < _nextDeviceScan) return;
                _nextDeviceScan = Time.unscaledTime + 1f;
                DeviceDiagnostics();
                _device = FindDevice();
                if (_device == null) return;
                ResolveControls(_device);
                StartCapture();
                _unwrapBaselinePending = true;
                Log.LogInfo($"[BoatMod] wheel: device '{_device.displayName}' description='{_device.description.product}'");
                return;
            }

            if (!_captured)
            {
                CaptureRest();
                return;
            }

            if (_device != null)
            {
                bool present = false;
                try
                {
                    foreach (var d in InputSystem.devices)
                        if (d == _device) { present = true; break; }
                }
                catch { }
                if (!present)
                {
                    Log.LogWarning("[BoatMod] wheel: device vanished from InputSystem - rescanning");
                    _device = null;
                    _captured = false;
                    _steer = _throttle = _brake = null;
                    _injectOn = false;
                    _wheelButtons.Clear();
                    _boostCtl.Clear();
                    _boostNames.Clear();
                    _learnPending.Clear();
                    return;
                }
            }

            UpdateButtons();

            Vector3 raw;
            float steerUnproc;
            try
            {
                raw = new Vector3(
                    _steer != null ? _steer.ReadValue() : 0f,
                    _throttle != null ? _throttle.ReadValue() : 0f,
                    _brake != null ? _brake.ReadValue() : 0f);
                steerUnproc = _steer != null ? _steer.ReadUnprocessedValue() : 0f;
                if (_unwrapBaselinePending) ResetUnwrapBaseline(steerUnproc);
                else TrackUnwrap(steerUnproc);
            }
            catch
            {
                _device = null;
                _captured = false;
                _steer = _throttle = _brake = null;
                _injectOn = false;
                return;
            }

            float dt = Time.unscaledTime - _lastFrame;
            _lastFrame = Time.unscaledTime;
            if (dt <= 0f || dt > 0.5f) dt = 0.016f;
            float pedalSmooth = Mathf.Clamp01(1f - Mathf.Exp(-dt / Mathf.Max(0.001f, _smoothing.Value)));
            float steerSmooth = Mathf.Clamp01(1f - Mathf.Exp(-dt / Mathf.Max(0.001f, _steerSmooth.Value)));

            _ring[_ringIdx] = _unwrappedSteer;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;

            if (HandleCalibration(_unwrappedSteer)) return;

            float throttleRaw = Remap(raw.y, _restThrottle, ref _throttleRange, symmetric: false);
            float brakeRaw = Remap(raw.z, _restBrake, ref _brakeRange, symmetric: false);
            if (_throttleInv.Value) throttleRaw = -throttleRaw;
            if (_brakeInv.Value) brakeRaw = -brakeRaw;
            throttleRaw = ApplyDeadzone(throttleRaw, _pedalDeadzone.Value);
            brakeRaw = ApplyDeadzone(brakeRaw, _pedalDeadzone.Value);

            float steerRaw = _calStep != 0 ? 0f : ComputeSteerSource(steerUnproc);
            if (_steerInv.Value) steerRaw = -steerRaw;

            float duAxis = Math.Abs(steerUnproc - _prevU);
            if (duAxis > 0.03f) _touchedOnce = true;

            if (_calStep == 0 && !_sessionHasCal && PersistentCalUsable() && _prevMappedValid && _touchedOnce)
            {
                if (duAxis < 0.3f && Math.Abs(steerRaw - _prevMappedRaw) > 0.6f && !_reanchorArmed)
                {
                    _reanchorArmed = true;
                    Log.LogWarning("[BoatMod] wheel: steering seam detected away from the lock stop (device axis zero changed since calibration) - hold any FULL lock ~0.5s to re-anchor");
                    CalFeedback("steering misaligned (device re-zeroed?) - turn to any FULL lock and hold ~0.5s");
                }
                if (duAxis < 0.01f)
                {
                    _pinTime += dt;
                    _pinDirSum += steerUnproc - _prevU;
                    float span = _calSteerRawSpan.Value;
                    bool spanIsFullTurn = Math.Abs(span - 2f) < 0.02f;
                    if (_pinTime > 0.35f && _reanchorArmed && (spanIsFullTurn || Math.Abs(_pinDirSum) > 0.15f))
                    {
                        float newLeft = spanIsFullTurn || _pinDirSum < 0f
                            ? WrapCircle(steerUnproc)
                            : WrapCircle(steerUnproc - span * _calSteerTurnSign.Value);
                        _calSteerRawLeft.Value = newLeft;
                        _cfg?.Save();
                        Log.LogInfo($"[BoatMod] wheel: steering re-anchored at lock stop (raw left {newLeft:F3}, span {span:F2})");
                        CalFeedback($"steering re-anchored (raw left {newLeft:F3}) - drive on");
                        _reanchorArmed = false;
                        _pinTime = 0f;
                        _pinDirSum = 0f;
                        _prevMappedValid = false;
                    }
                }
                else
                {
                    _pinTime = 0f;
                    _pinDirSum = 0f;
                }
            }
            _prevMappedRaw = steerRaw;
            _prevU = steerUnproc;
            _prevMappedValid = true;

            _smSteer += (steerRaw - _smSteer) * steerSmooth;
            _smThrottle += (throttleRaw - _smThrottle) * pedalSmooth;
            _smBrake += (brakeRaw - _smBrake) * pedalSmooth;

            _outSteer = Mathf.Clamp(_smSteer, -1f, 1f);
            _outPedal = Mathf.Clamp(_smThrottle - _smBrake, -1f, 1f);
            _injectOn = true;
            if (!_touchedOnce)
            {
                _smSteer = 0f;
                _outSteer = 0f;
            }

            if (_diag != null && _diag.Value && Time.unscaledTime - _nextDiagLog > 5f)
            {
                _nextDiagLog = Time.unscaledTime;
                Log.LogInfo($"[BoatMod] wheel: steer={_outSteer:F2} pedal={_outPedal:F2} raw(r={raw.x:F2} u={steerUnproc:F2} w={_unwrappedSteer:F2} t={raw.y:F2} b={raw.z:F2}) ranges(t={_throttleRange:F2} b={_brakeRange:F2})");
            }
        }

        private static void ToggleFullscreen()
        {
            try
            {
                bool goingFull = Screen.fullScreenMode != FullScreenMode.FullScreenWindow;
                if (goingFull && Screen.width > 0 && Screen.height > 0)
                {
                    _winW = Screen.width;
                    _winH = Screen.height;
                }
                ApplyFullscreen(goingFull);
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] display: toggle failed: {e.Message}");
            }
        }

        private static void ApplyFullscreen(bool full)
        {
            try
            {
                var disp = Display.main;
                if (full)
                {
                    Screen.SetResolution(disp.systemWidth, disp.systemHeight, FullScreenMode.FullScreenWindow);
                    Log.LogInfo($"[BoatMod] display: borderless fullscreen {disp.systemWidth}x{disp.systemHeight} (F11 to undo)");
                }
                else
                {
                    int w = _winW > 0 ? _winW : disp.systemWidth * 3 / 4;
                    int h = _winH > 0 ? _winH : disp.systemHeight * 3 / 4;
                    if (w >= disp.systemWidth || h >= disp.systemHeight)
                    {
                        w = disp.systemWidth * 3 / 4;
                        h = disp.systemHeight * 3 / 4;
                    }
                    Screen.SetResolution(w, h, FullScreenMode.Windowed);
                    Log.LogInfo($"[BoatMod] display: windowed {w}x{h}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] display: apply failed: {e.Message}");
            }
        }

        #region Steering calibration

        private static Key? ParseNamedKey(ConfigEntry<string> entry)
        {
            var text = entry?.Value;
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                if (Enum.TryParse(text, true, out Key parsed)) return parsed;
            }
            catch { }
            if (text.Length == 1)
            {
                char c = char.ToUpperInvariant(text[0]);
                if (c >= 'A' && c <= 'Z') return (Key)(c - 'A' + 15);   // Key enum: A=15 .. Z=40
                if (c >= '1' && c <= '9') return (Key)(c - '1' + 41);   // Digit1=41 .. Digit9=49
                if (c == '0') return (Key)50;                            // Digit0=50
            }
            return null;
        }

        private static bool KeyDownNow(Key? key)
        {
            if (!key.HasValue) return false;
            try
            {
                var kb = Keyboard.current;
                var btn = kb != null ? kb[key.Value] : null;
                return btn != null && btn.wasPressedThisFrame;
            }
            catch (Exception e)
            {
                if (!_calLoggedStep)
                {
                    _calLoggedStep = true;
                    Log.LogWarning($"[BoatMod] wheel: cal key read failed: {e.Message}");
                }
                return false;
            }
        }

        private static bool HandleCalibration(float rawSteer)
        {
            if (KeyDownNow(ParseNamedKey(_calKey)))
                NextCalStep();

            if (_calStep == 0) return false;

            float now = Time.unscaledTime;
            if (now - _calLiveAt > 1f)
            {
                _calLiveAt = now;
                string stepName = _calStep == 1 ? "left-hold" : "right-hold";
                Log.LogInfo($"[BoatMod] wheel: cal ({stepName}) live raw={rawSteer:F3}");
            }

            switch (_calStep)
            {
                case 1:
                    _calSum += rawSteer;
                    _calSamples++;
                    if (rawSteer < _wipLeft) _wipLeft = rawSteer;
                    return false;
                case 2:
                    _calSum += rawSteer;
                    _calSamples++;
                    if (rawSteer > _wipRight) _wipRight = rawSteer;
                    return false;
            }
            return false;
        }

        private static float RingRecentMin(int n)
        {
            n = Math.Min(Math.Min(n, _ringCount), _ring.Length);
            if (n <= 0) return 0f;
            float lo = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float v = _ring[(_ringIdx - 1 - i + _ring.Length * 2) % _ring.Length];
                if (v < lo) lo = v;
            }
            return lo;
        }

        private static float RingRecentMax(int n)
        {
            n = Math.Min(Math.Min(n, _ringCount), _ring.Length);
            if (n <= 0) return 0f;
            float hi = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float v = _ring[(_ringIdx - 1 - i + _ring.Length * 2) % _ring.Length];
                if (v > hi) hi = v;
            }
            return hi;
        }

        private static void NextCalStep()
        {
            float now = Time.unscaledTime;
            switch (_calStep)
            {
                case 0:
                    _calStep = 1;
                    _guiVisible = true;
                    _calStartedAt = now;
                    _calSum = 0f;
                    _calSamples = 0;
                    _wipLeft = 10f;
                    _wipRight = -10f;
                    _wipCenter = 0f;
                    CalFeedback("wizard started - keep overlay open, follow the steps");
                    Log.LogInfo("[BoatMod] wheel: CALIBRATION started - step 1/3: TURN WHEEL FULL LEFT (against the stop) AND HOLD, press key again");
                    break;
                case 1:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: left sample too short, press key again"); return; }
                        float recentMin = RingRecentMin(32);
                        if (Math.Abs(recentMin - _wipLeft) > 0.05f)
                        {
                            CalFeedback($"left lock not at stop right now ({recentMin:F3}) - FULL left, hold, press key");
                            Log.LogWarning($"[BoatMod] wheel: left lock not held recently (recent min {recentMin:F3} vs best {_wipLeft:F3}) - go FULL left against the stop, hold ~1s, press key");
                            return;
                        }
                        _calStep = 2;
                        _calStartedAt = now;
                        _calSum = 0f;
                        _calSamples = 0;
                        CalFeedback($"left lock {_wipLeft:F3} ACCEPTED - now turn FULL right");
                        Log.LogInfo($"[BoatMod] wheel: left lock = {_wipLeft:F3} - step 2/3: TURN WHEEL FULL RIGHT (against the stop) AND HOLD, press key again");
                        break;
                    }
                case 2:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: right sample too short, press key again"); return; }
                        float recentMax = RingRecentMax(32);
                        if (Math.Abs(recentMax - _wipRight) > 0.05f)
                        {
                            CalFeedback($"right lock not at stop right now ({recentMax:F3}) - FULL right, hold, press key");
                            Log.LogWarning($"[BoatMod] wheel: right lock not held recently (recent max {recentMax:F3} vs best {_wipRight:F3}) - go FULL right against the stop, hold ~1s, press key");
                            return;
                        }
                        if (Math.Abs(_wipRight - _wipLeft) < 0.5f)
                        {
                            CalFeedback($"REJECTED: locks too close (L {_wipLeft:F3} R {_wipRight:F3}) - retake");
                            Log.LogWarning($"[BoatMod] wheel: calibration degenerate (left {_wipLeft:F3} vs right {_wipRight:F3}, span < 0.5), NOT saved - previous calibration kept: " + CalStatusLine());
                            _calStep = 0;
                            _calStartedAt = 0f;
                            break;
                        }
                        _wipCenter = (_wipLeft + _wipRight) / 2f;
                        _calStep = 0;
                        _calStartedAt = 0f;
                        _sessionHasCal = true;
                        _calSteerCenter.Value = _wipCenter;
                        _calSteerLeft.Value = _wipLeft;
                        _calSteerRight.Value = _wipRight;
                        _calSteerSaved.Value = true;
                        _calSteerRawLeft.Value = WrapCircle(_wipLeft + _unwrapBaselineRaw);
                        _calSteerRawSpan.Value = _wipRight - _wipLeft;
                        _calSteerTurnSign.Value = _wipRight >= _wipLeft ? 1f : -1f;
                        _cfg?.Save();
                        _prevMappedValid = false;
                        _reanchorArmed = false;
                        _pinTime = 0f;
                        _pinDirSum = 0f;
                        CalFeedback($"SAVED (persists across restarts): center {_wipCenter:F3} (L {_wipLeft:F3} R {_wipRight:F3}) - go drive!");
                        Log.LogInfo($"[BoatMod] wheel: CALIBRATION COMPLETE - center={_wipCenter:F3} from locks L={_wipLeft:F3} R={_wipRight:F3}; persistent: rawLeft={_calSteerRawLeft.Value:F3} span={_calSteerRawSpan.Value:F2} sign={_calSteerTurnSign.Value:F0}");
                        break;
                    }
            }
        }

        private static bool _sessionHasCal;
        private static ConfigFile _cfg;

        private static bool PersistentCalUsable()
        {
            return _calSteerSaved != null && _calSteerSaved.Value
                && _calSteerRawSpan.Value > 0.3f && _calSteerRawSpan.Value <= 2.05f
                && Math.Abs(_calSteerTurnSign.Value) > 0.5f;
        }

        private static float ComputeSteerSource(float steerUnprocRaw)
        {
            if (_sessionHasCal)
                return ShapeSteer(SteerUnwrappedToOut(_unwrappedSteer));
            if (PersistentCalUsable())
                return ShapeSteer(PersistentSteer(steerUnprocRaw));
            return 0f;
        }

        private static float SteerUnwrappedToOut(float raw)
        {
            float center = _calSteerCenter.Value;
            if (Math.Abs(center) > 0.35f) return 0f;   // implausible saved center -> treat as uncalibrated, keyboard passes through
            float left = _calSteerLeft.Value;
            float right = _calSteerRight.Value;
            float outv;
            if (raw >= center)
            {
                float span = right - center;
                outv = span > 0.0001f ? (raw - center) / span : 0f;
            }
            else
            {
                float span = center - left;
                outv = span > 0.0001f ? (raw - center) / span : 0f;
            }
            return Mathf.Clamp(outv, -1f, 1f);
        }

        private static float PersistentSteer(float raw)
        {
            float span = _calSteerRawSpan.Value;
            float sign = _calSteerTurnSign.Value;
            float d = WrapCycle((raw - _calSteerRawLeft.Value) * sign);
            if (d < 0.02f && _outSteer > 0.5f) d = span;   // parked on the right lock: the locks share one circle point
            if (d > span) d = span;
            return d / (span * 0.5f) - 1f;
        }

        private static float ShapeSteer(float outv)
        {
            outv = Mathf.Clamp(outv, -1f, 1f);
            float dz = Mathf.Max(0f, _steerDeadzone.Value);
            if (Mathf.Abs(outv) <= dz) return 0f;
            float curve = Mathf.Max(0.1f, _steerCurve.Value);
            float shaped = Mathf.Sign(outv) * Mathf.Pow(Mathf.Abs(outv), curve);
            if (Mathf.Abs(shaped) < Mathf.Max(0f, _centerSnap.Value)) return 0f;
            return shaped;
        }

        private static float WrapCircle(float v)
        {
            return v - 2f * Mathf.Floor((v + 1f) / 2f);
        }

        private static float WrapCycle(float v)
        {
            return v - 2f * Mathf.Floor(v / 2f);
        }

        #endregion

        private static void InitInputRefs(Type t, out FieldInfo field, out FieldInfo enableField)
        {
            if (!_motorInputFields.TryGetValue(t, out field))
            {
                field = t.GetField("_motorInput", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _motorInputFields[t] = field;
            }
            if (!_inputEnabledFields.TryGetValue(t, out enableField))
            {
                enableField = t.GetField("IsInputEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _inputEnabledFields[t] = enableField;
            }
        }

        private static void MotorInputPrefix(object __instance)
        {
            if (!_injectOn) return;
            try
            {
                var t = __instance.GetType();
                if (!t.Name.Contains("BoatInput")) return;
                InitInputRefs(t, out var field, out var enableField);
                if (field == null) return;
                if (enableField != null && !Equals(true, enableField.GetValue(__instance))) return;
                _lastHandler = __instance;
                var current = (Vector2)field.GetValue(__instance);
                var merged = new Vector2(
                    Mathf.Abs(_outSteer) > 0.001f ? -_outSteer : current.x,
                    Mathf.Abs(_outPedal) > 0.001f ? _outPedal : current.y);
                field.SetValue(__instance, merged);
            }
            catch { }
        }

        private static void MotorInputPostfix(object __instance)
        {
            if (_directRudder == null || !_directRudder.Value) return;
            try
            {
                var t = __instance.GetType();
                if (!t.Name.Contains("BoatInput")) return;
                InitInputRefs(t, out var field, out var enableField);
                if (field == null) return;
                if (enableField != null && !Equals(true, enableField.GetValue(__instance))) return;
                if (!_motorAngleFields.TryGetValue(t, out var angleField))
                {
                    angleField = t.GetField("_motorAngle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _motorAngleFields[t] = angleField;
                }
                if (!_motorFields.TryGetValue(t, out var motorField))
                {
                    motorField = t.GetField("_motor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _motorFields[t] = motorField;
                }
                if (angleField == null || motorField == null) return;
                var mi = (Vector2)field.GetValue(__instance);
                float x = Mathf.Clamp(mi.x, -1f, 1f);
                if (Mathf.Abs(x) < 0.0005f) x = 0f;
                float angle = 0.5f + 0.5f * x;
                angleField.SetValue(__instance, angle);
                var motor = motorField.GetValue(__instance);
                if (motor == null) return;
                var motorType = motor.GetType();
                if (!_motorSetAngle.TryGetValue(motorType, out var setAngle))
                {
                    setAngle = motorType.GetMethod("SetAngle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
                    _motorSetAngle[motorType] = setAngle;
                }
                setAngle?.Invoke(motor, new object[] { angle });
            }
            catch { }
        }

        internal static void SetDiag(ConfigEntry<bool> diag)
        {
            _diag = diag;
        }

        private static void ResolveControls(InputDevice dev)
        {
            _steer = FindAxis(dev, _steerCtl.Value, "steer", new[] { "stick/x", "x" }, null, null);
            _throttle = FindAxis(dev, _throttleCtl.Value, "throttle", new[] { "z", "trigger", "throttle", "accelerator", "accel", "gas" }, _steer, null);
            _brake = FindAxis(dev, _brakeCtl.Value, "brake", new[] { "rz", "brake", "brakepedal" }, _steer, _throttle);
            if (_steer == null || _throttle == null || _brake == null)
            {
                var missing = new List<string>();
                if (_steer == null) missing.Add(_steerCtl.Value);
                if (_throttle == null) missing.Add(_throttleCtl.Value);
                if (_brake == null) missing.Add(_brakeCtl.Value);
                Log.LogWarning($"[BoatMod] wheel: missing control(s) [{string.Join(", ", missing)}] on '{dev.displayName}' - check the control dump below and set SteerControl/ThrottleControl/BrakeControl in the config");
            }
            BuildButtonList(dev);
            DumpControls(dev);
        }

        private static AxisControl FindAxis(InputDevice dev, string configured, string role, string[] autoNames, AxisControl skipA, AxisControl skipB)
        {
            AxisControl byPath = null;
            try { byPath = dev.TryGetChildControl<AxisControl>(configured); } catch { }
            if (byPath != null && byPath != skipA && byPath != skipB) return byPath;

            AxisControl byName = null, byAuto = null;
            try
            {
                string want = (configured ?? "").ToLowerInvariant();
                foreach (var c in dev.allControls)
                {
                    var a = c as AxisControl;
                    if (a == null || a == skipA || a == skipB) continue;
                    var n = (c.name ?? "").ToLowerInvariant();
                    var p = c.path ?? "";
                    var leaf = p.LastIndexOf('/') >= 0 ? p.Substring(p.LastIndexOf('/') + 1).ToLowerInvariant() : p;
                    if (byName == null && want.Length > 0 && (n == want || leaf == want)) byName = a;
                    if (byAuto == null)
                        foreach (var t in autoNames)
                            if (n == t.ToLowerInvariant() || leaf == t.ToLowerInvariant()) { byAuto = a; break; }
                }
            }
            catch { }
            var hit = byName ?? byAuto;
            if (hit != null)
                Log.LogInfo($"[BoatMod] wheel: {role} axis resolved to '{hit.path}' (configured '{configured}'{(byPath == null ? ", auto-matched" : "")})");
            return hit;
        }

        private static void BuildButtonList(InputDevice dev)
        {
            _wheelButtons.Clear();
            _wheelButtonNames.Clear();
            _wheelButtonPaths.Clear();
            _wheelButtonLast.Clear();
            try
            {
                foreach (var c in dev.allControls)
                {
                    var b = c as ButtonControl;
                    if (b == null) continue;
                    _wheelButtons.Add(b);
                    _wheelButtonNames.Add(c.name);
                    _wheelButtonPaths.Add(c.path);
                    _wheelButtonLast.Add(false);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BoatMod] wheel: button scan failed: " + e.Message);
            }
            Log.LogInfo($"[BoatMod] wheel: buttons found ({_wheelButtons.Count}): " + string.Join(", ", _wheelButtonNames.ToArray()));
            InitBoostRuntime();
        }

        private static void InitBoostRuntime()
        {
            _boostCtl.Clear();
            _boostNames.Clear();
            _learnPending.Clear();
            string def = _boostButtons != null ? (_boostButtons.Value ?? "") : "";
            if (string.IsNullOrEmpty(def.Trim()))
            {
                Log.LogInfo("[BoatMod] wheel: boost UNBOUND - learning mode: the first two distinct wheel button presses will be saved and armed");
                return;
            }
            foreach (var rawTok in def.Split(';'))
            {
                var name = rawTok.Trim();
                if (name.Length == 0) continue;
                int idx = -1;
                for (int i = 0; i < _wheelButtonNames.Count; i++)
                    if (string.Equals(_wheelButtonNames[i], name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                if (idx < 0)
                {
                    Log.LogWarning($"[BoatMod] wheel: boost button '{name}' not found on device '{(_device != null ? _device.displayName : "?")}'");
                    continue;
                }
                _boostCtl.Add(_wheelButtons[idx]);
                _boostNames.Add(name);
            }
            if (_boostCtl.Count > 0)
                Log.LogInfo($"[BoatMod] wheel: boost armed from BoostButtons='{def}': {string.Join(" / ", _boostNames.ToArray())} - each press mirrors the Space Supercharge");
        }

        private static void UpdateButtons()
        {
            try
            {
                bool learnMode = _boostCtl.Count == 0 && string.IsNullOrEmpty(_boostButtons.Value.Trim());
                for (int i = 0; i < _wheelButtons.Count; i++)
                {
                    bool up;
                    try { up = _wheelButtons[i].IsPressed(); }
                    catch { up = false; }
                    if (up && !_wheelButtonLast[i]) OnButtonEdge(i, learnMode);
                    _wheelButtonLast[i] = up;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BoatMod] wheel: button poll failed: " + e.Message);
            }
        }

        private static void OnButtonEdge(int idx, bool learnMode)
        {
            var name = _wheelButtonNames[idx];
            Log.LogInfo($"[BoatMod] wheel: BUTTON press '{name}' ({_wheelButtonPaths[idx]})");
            if (learnMode)
            {
                if (_learnPending.Contains(name))
                {
                    CalFeedback($"boost capture: '{name}' already taken - press a DIFFERENT second button");
                    return;
                }
                _learnPending.Add(name);
                if (_learnPending.Count >= 2)
                {
                    _boostButtons.Value = string.Join(";", _learnPending.ToArray());
                    _cfg?.Save();
                    InitBoostRuntime();
                    CalFeedback($"BOOST SAVED + armed: {_boostButtons.Value} (persists across restarts)");
                    Log.LogInfo($"[BoatMod] wheel: boost bindings SAVED -> BoostButtons='{_boostButtons.Value}'; both buttons now mirror the Space Supercharge");
                }
                else
                {
                    CalFeedback($"boost capture: '{name}' taken (1/2) - now press the SECOND button");
                    Log.LogInfo("[BoatMod] wheel: boost capture 1/2 done - press the SECOND button");
                }
                return;
            }
            if (_boostNames.Contains(name)) FireBoost(name);
        }

        private static void FireBoost(string name)
        {
            float now = Time.unscaledTime;
            if (now - _boostLastFire < 0.12f) return;
            _boostLastFire = now;
            var handler = ResolveHandlerInstance();
            if (handler == null)
            {
                Log.LogWarning("[BoatMod] wheel: boost pressed but no boat input handler alive yet");
                return;
            }
            bool driving = HandlerInputEnabled(handler);
            var t = handler.GetType();
            if (!_boostMethods.TryGetValue(t, out var m))
            {
                m = t.GetMethod("OnSuperchargerUsed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                _boostMethods[t] = m;
            }
            if (m == null)
            {
                Log.LogWarning("[BoatMod] wheel: boost pressed but OnSuperchargerUsed() not found on " + t.Name);
                return;
            }
            try
            {
                object res = m.Invoke(handler, null);
                bool ok = res is bool b ? b : true;
                if (ok)
                {
                    Log.LogInfo($"[BoatMod] wheel: boost '{name}' -> {t.Name} supercharge fired");
                }
                else
                {
                    string why = "unknown gate";
                    try
                    {
                        var sup = t.GetField("_supercharger", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(handler);
                        if (sup == null) why = "no Supercharger component wired on this handler";
                        else
                        {
                            var cc = sup.GetType().GetProperty("ChargeCount")?.GetValue(sup, null);
                            var seM = t.GetMethod("IsSuperchargerEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var se = seM != null ? seM.Invoke(handler, null) : null;
                            why = $"charge={(cc ?? (object)"?")} superchargerEnabled={(se ?? (object)"n/a")}";
                        }
                    }
                    catch (Exception ex) { why = "gate check failed: " + ex.Message; }
                    Log.LogWarning($"[BoatMod] wheel: boost '{name}' REFUSED by {t.Name} (input {(driving ? "on" : "off")}): {why}");
                    if (driving) CalFeedback("boost refused: " + why);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BoatMod] wheel: boost invoke failed: " + e.Message);
            }
        }

        private static void SuperchargeInputPostfix(object __instance)
        {
            _spaceHandler = __instance;
        }

        private static bool HandlerInputEnabled(object h)
        {
            try
            {
                InitInputRefs(h.GetType(), out _, out var enableField);
                return enableField == null || Equals(true, enableField.GetValue(h));
            }
            catch { return false; }
        }

        private static int HandlerSpaceScore(object h)
        {
            try
            {
                int score = 0;
                if (HandlerInputEnabled(h)) score++;
                var t = h.GetType();
                var coll = t.GetField("_inputActions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(h);
                if (coll == null) return score;
                var player = coll.GetType().GetProperty("Player")?.GetValue(coll, null);
                var action = player != null ? player.GetType().GetProperty("Supercharge")?.GetValue(player, null) as InputAction : null;
                if (action != null && action.enabled) score += 2;
                return score;
            }
            catch { return 0; }
        }

        private static object ResolveHandlerInstance()
        {
            try
            {
                var viaSpace = _spaceHandler as UnityEngine.Object;
                if (viaSpace != null) return _spaceHandler;

                object best = null, enabled = null;
                int bestScore = -1;
                foreach (var name in new[] { "BoatInputActionsHandler", "BoatInputHandler" })
                {
                    var t = FindType(name);
                    if (t == null) continue;
                    foreach (var found in UnityEngine.Object.FindObjectsOfType(t))
                    {
                        int score = HandlerSpaceScore(found);
                        if (HandlerInputEnabled(found) && enabled == null) enabled = found;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = found;
                        }
                    }
                }
                if (best != null && bestScore >= 1)
                {
                    _lastHandler = best;
                    return best;
                }
                if (enabled != null)
                {
                    _lastHandler = enabled;
                    return enabled;
                }
                if (best != null)
                {
                    _lastHandler = best;
                    return best;
                }
            }
            catch { }
            return null;
        }

        private static void DumpControls(InputDevice dev)
        {
            try
            {
                Log.LogInfo($"[BoatMod] wheel controls on '{dev.displayName}':");
                foreach (var c in dev.allControls)
                    Log.LogInfo($"[BoatMod]   '{c.name}' layout='{c.layout}' v={(c.ReadValueAsObject() ?? "(null)")}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] wheel control dump failed: {e.Message}");
            }
        }

        private static InputDevice FindDevice()
        {
            try
            {
                var match = (_deviceName.Value ?? "").Trim();
                var autoTokens = new[] { "moza", "gudsen", "racing", "wheel", "r3" };
                InputDevice fallback = null;
                foreach (var d in InputSystem.devices)
                {
                    if (d is Keyboard || d is Mouse || d is Touchscreen || d is Gamepad) continue;
                    bool hasAxis = false;
                    try { foreach (var c in d.allControls) { if (c is AxisControl) { hasAxis = true; break; } } } catch { }
                    if (!hasAxis) continue;
                    var all = (d.displayName ?? "") + " " + (d.description.product ?? "") + " " + (d.description.manufacturer ?? "");
                    if (match.Length > 0)
                    {
                        if (all.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log.LogInfo($"[BoatMod] wheel: device match '{d.displayName}' (type '{d.GetType().Name}', layout '{d.layout}') via DeviceName='{match}'");
                            return d;
                        }
                        continue;
                    }
                    bool autoHit = false;
                    foreach (var tok in autoTokens)
                        if (all.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0) { autoHit = true; break; }
                    if (autoHit)
                    {
                        Log.LogInfo($"[BoatMod] wheel: device auto-match '{d.displayName}' (type '{d.GetType().Name}', layout '{d.layout}')");
                        return d;
                    }
                    if (fallback == null) fallback = d;
                }
                if (fallback != null)
                    Log.LogInfo($"[BoatMod] wheel: no name match, using first axis device '{fallback.displayName}' (type '{fallback.GetType().Name}', layout '{fallback.layout}')");
                return fallback;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] wheel scan failed: {e.Message}");
            }
            return null;
        }

        private static void StartCapture()
        {
            _captureSamples = 0;
            _restThrottle = _restBrake = 0f;
            _captureUntil = Time.unscaledTime + 1f;
        }

        private static void CaptureRest()
        {
            if (Time.unscaledTime > _captureUntil)
            {
                if (_captureSamples > 0)
                {
                    _restThrottle /= _captureSamples;
                    _restBrake /= _captureSamples;
                }
                _captured = true;
                _throttleRange = _brakeRange = 0.3f;
                if (_calSteerSaved.Value && PersistentCalUsable() && !_sessionHasCal)
                    Log.LogInfo($"[BoatMod] wheel: using PERSISTENT steering calibration (raw left {_calSteerRawLeft.Value:F3}, span {_calSteerRawSpan.Value:F2}, sign {_calSteerTurnSign.Value:F0}) - no wizard needed; press {_calKey.Value} to retake");
                else if (_calSteerSaved.Value)
                    Log.LogInfo($"[BoatMod] wheel: using session steering calibration center={_calSteerCenter.Value:F3} left={_calSteerLeft.Value:F3} right={_calSteerRight.Value:F3} (press {_calKey.Value} to recalibrate)");
                else
                    Log.LogInfo($"[BoatMod] wheel: no saved steering calibration - run wizard once (press {_calKey.Value}; after two lock captures it persists across restarts). pedals rest t={_restThrottle:F2} b={_restBrake:F2}");
                return;
            }
            if (_throttle == null && _brake == null) return;
            _restThrottle += _throttle != null ? _throttle.ReadValue() : 0f;
            _restBrake += _brake != null ? _brake.ReadValue() : 0f;
            _captureSamples++;
        }

        private static float Remap(float value, float rest, ref float range, bool symmetric)
        {
            float delta = value - rest;
            if (!symmetric && delta < 0f) delta = 0f;
            float mag = Mathf.Abs(delta);
            if (mag > range) range = mag;
            if (range <= 0.0001f) return 0f;
            return Mathf.Clamp(delta / range, -1f, 1f);
        }

        private static float ApplyDeadzone(float v, float dz)
        {
            if (Mathf.Abs(v) <= dz) return 0f;
            return Mathf.Sign(v) * (Mathf.Abs(v) - dz) / (1f - dz);
        }

        private static void DeviceDiagnostics()
        {
            try
            {
                foreach (var d in InputSystem.devices)
                {
                    var nm = d.displayName;
                    if (_unloggedDevices.Add(d.GetType().Name + "|" + nm))
                        Log.LogInfo($"[BoatMod] wheel: device found type='{d.GetType().Name}' name='{nm}' layout='{d.layout}' product='{d.description.product}'");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] wheel: device diag failed: {e.Message}");
            }
        }

        private static readonly HashSet<string> _unloggedDevices = new HashSet<string>();

        private static void BindConfig(ConfigFile cfg)
        {
            _cfg = cfg;
            _enabled = cfg.Bind("Wheel", "Enabled", true, "Drive the boat with the MOZA R3 wheel/pedals (master switch)");
            _deviceName = cfg.Bind("Wheel", "DeviceName", "", "Substring match against device name/product/manufacturer (empty = auto: prefer Moza/Gudsen/wheel-like devices, else first axis device)");
            _steerCtl = cfg.Bind("Wheel", "SteerControl", "stick/x", "Wheel axis control path (see wheel control dump in the log)");
            _throttleCtl = cfg.Bind("Wheel", "ThrottleControl", "z", "Throttle pedal axis control name");
            _brakeCtl = cfg.Bind("Wheel", "BrakeControl", "rz", "Brake pedal axis control name");
            _steerInv = cfg.Bind("Wheel", "SteerInvert", false, "Invert steering direction");
            _throttleInv = cfg.Bind("Wheel", "ThrottleInvert", false, "Invert throttle pedal direction");
            _brakeInv = cfg.Bind("Wheel", "BrakeInvert", false, "Invert brake pedal direction");
            _steerDeadzone = cfg.Bind("Wheel", "SteerDeadzone", 0.02f, "Steering deadzone at wheel center as a GATE (below it: nothing, above it: untouched linear value)");
            _pedalDeadzone = cfg.Bind("Wheel", "PedalDeadzone", 0.05f, "Deadzone for pedals (0..1, rescaled/companding)");
            _smoothing = cfg.Bind("Wheel", "SmoothingSeconds", 0.15f, "Pedal smoothing window in seconds (lower = snappier)");
            _steerSmooth = cfg.Bind("Wheel", "SteerSmoothingSeconds", 0.05f, "Steering-only smoothing window (lower = snappier, tracks wheel directly)");
            _centerSnap = cfg.Bind("Wheel", "SteerCenterSnap", 0.015f, "Below this mapped deflection output exactly 0 so the game auto-centers the rudder (kills drift)");
            _steerCurve = cfg.Bind("Wheel", "SteerCurve", 1f, "Steering linearity exponent (1.0 = linear; 1.5 = softer center, sharper locks)");
            _directRudder = cfg.Bind("Wheel", "DirectRudder", false, "Postfix overrides rudder angle directly each tick (crisp 1:1, skips the game's easing). Set true if steering still feels laggy/mushy");
            _calKey = cfg.Bind("Wheel", "CalKey", "K", "Keyboard key that advances the steering calibration wizard (A-Z, 0-9)");
            _boostLearnKey = cfg.Bind("Wheel", "BoostLearnKey", "L", "Keyboard key that clears the boost binding and re-enters learn mode (the next two distinct wheel button presses are saved)");
            _guiKey = cfg.Bind("Wheel", "DebugKey", "F3", "Keyboard key that toggles the wheel debug overlay (show during calibration regardless)");
            _boostButtons = cfg.Bind("Wheel", "BoostButtons", "", "Semicolon-separated wheel button names that mirror the Space Supercharge. Leave EMPTY to learn: the first two distinct button presses on the wheel are saved here automatically");

            _fsKey = cfg.Bind("Display", "FullscreenKey", "F11", "Keyboard key that toggles borderless fullscreen at the monitor's native resolution (Windows Alt+Enter is unreliable in this Unity version)");
            _fsStart = cfg.Bind("Display", "StartFullscreen", false, "Start the game in borderless fullscreen at monitor resolution");

            _calSteerSaved = cfg.Bind("Wheel", "SteerCalibrated", false, "Internal: steering calibration present");
            _calSteerCenter = cfg.Bind("Wheel", "SteerCenter", 0f, "Internal: calibrated steering center (session raw value)");
            _calSteerLeft = cfg.Bind("Wheel", "SteerLeft", 0f, "Internal: full LEFT lock raw value");
            _calSteerRight = cfg.Bind("Wheel", "SteerRight", 0f, "Internal: full RIGHT lock raw value");
            _calSteerRawLeft = cfg.Bind("Wheel", "SteerRawLeft", 0f, "Internal: LEFT lock on the hardware angle circle (persists across restarts)");
            _calSteerRawSpan = cfg.Bind("Wheel", "SteerRawSpan", 0f, "Internal: lock-to-lock distance in unwrapped units (~2.0 for a 900 degree wheel)");
            _calSteerTurnSign = cfg.Bind("Wheel", "SteerTurnSign", 1f, "Internal: +1 if turning right increases the raw axis, -1 otherwise");
        }

        private static Type FindType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(simpleName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static string BoostStatusLine()
        {
            if (_learnPending.Count > 0)
                return $"BOOST LEARNING: captured {_learnPending.Count}/2 ({string.Join(" + ", _learnPending.ToArray())}) - press the second button";
            if (_boostCtl.Count > 0)
                return "boost: " + string.Join(" / ", _boostNames.ToArray()) + " (mirrors Space; " + (_boostLearnKey?.Value ?? "L") + " to rebind)";
            return "boost: UNBOUND - press two wheel buttons to bind (first two distinct presses are saved)";
        }

        private static string CalStatusLine()
        {
            if (_sessionHasCal)
                return $"cal: center {_calSteerCenter.Value:F3}  L {_calSteerLeft.Value:F3}  R {_calSteerRight.Value:F3}";
            if (PersistentCalUsable())
                return $"PERSISTENT cal active (restarts OK): left@{WrapCircle(_calSteerRawLeft.Value):F3} span {_calSteerRawSpan.Value:F2} (wizard not needed; {_calKey?.Value ?? "K"} to retake)";
            return "not calibrated - press " + (_calKey?.Value ?? "K") + " to run the wizard once (saved calibration then survives restarts)";
        }

        private static string CalStepText()
        {
            switch (_calStep)
            {
                case 1: return "STEP 1/3 - TURN WHEEL FULL LEFT (against the stop) AND HOLD, press " + (_calKey?.Value ?? "K");
                case 2: return "STEP 2/3 - TURN WHEEL FULL RIGHT (against the stop) AND HOLD, press " + (_calKey?.Value ?? "K");
                default: return null;
            }
        }

        private static string BuildOverlayText()
        {
            var lines = new List<string>
            {
                "[F3] toggle  [F11] fullscreen  --  wheel: " + (_device == null ? "(no device yet)" : _device.displayName),
                CalStatusLine(),
                BoostStatusLine(),
            };
            if (BoatModPlugin.ModelFallback)
                lines.Add("model: load failed - using original boat visuals");
            if (_calStep != 0)
            {
                lines.Add(CalStepText());
                lines.Add($"cal: raw {_steer?.ReadValue().ToString("F3") ?? "?"}  unwrapped {_unwrappedSteer:F3}");
                lines.Add($"wip: L {_wipLeft:F3}  R {_wipRight:F3}  (center auto-derived at the end)");
            }
            if (!string.IsNullOrEmpty(_calMsg) && Time.unscaledTime < _calMsgUntil)
                lines.Add(_calMsg);
            else
            {
                lines.Add($"out: steer {_outSteer:F2}  pedal {_outPedal:F2}  inject={(_injectOn ? "on" : "off")}  direct={(_directRudder != null && _directRudder.Value ? "on" : "off")}");
                if (_device != null && _steer != null && _throttle != null && _brake != null)
                    lines.Add($"raw: r {_steer.ReadValue():F3} (u {_steer.ReadUnprocessedValue():F3})  w {_unwrappedSteer:F3}  t {_throttle.ReadValue():F3}  b {_brake.ReadValue():F3}");
                if (_diag != null && _diag.Value)
                    lines.Add("device: " + (_device != null ? (_device.description.product ?? "?") : "?"));
            }
            return string.Join("\n", lines.ToArray());
        }

        private static Canvas _uiCanvas;
        private static GameObject _uiPanelGO;
        private static UnityEngine.UI.Text _uiText;
        private static TMPro.TextMeshProUGUI _tmpText;
        private static TMPro.TMP_FontAsset _tmpFont;
        private static bool _uiTextFailed;
        private static bool _tmpLogged;
        private static float _nextUiTry;
        private static float _nextUiTextUpdate;
        private static bool _guiFired;

        private static void ResolveTmpFont()
        {
            if (_tmpFont != null) return;
            try
            {
                var names = new List<string>();
                foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    names.Add(f.name);
                    if (_tmpFont == null && f.name.IndexOf("SDF", StringComparison.OrdinalIgnoreCase) >= 0) _tmpFont = f;
                }
                if (_tmpFont == null && names.Count > 0)
                {
                    foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
                        if (f != null && !string.IsNullOrEmpty(f.name)) { _tmpFont = f; break; }
                }
                if (!_tmpLogged)
                {
                    _tmpLogged = true;
                    Log.LogInfo("[BoatMod] wheel: TMP fonts in memory: " + (names.Count > 0 ? string.Join(", ", names.GetRange(0, Math.Min(10, names.Count)).ToArray()) : "(none!)"));
                    if (_tmpFont != null) Log.LogInfo($"[BoatMod] wheel: TMP font picked = '{_tmpFont.name}'");
                    else if (names.Count > 0) Log.LogWarning("[BoatMod] wheel: TMP font not picked despite assets existing");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[BoatMod] wheel: TMP font scan failed: " + e.Message);
            }
        }

        private static void EnsureUiText()
        {
            bool want = _guiVisible || _calStep != 0;
            SetOverlayActive(want);
            if (_uiTextFailed || !want) return;
            if (_nextUiTry > Time.unscaledTime) return;
            _nextUiTry = Time.unscaledTime + 2f;
            try
            {
                if (_uiPanelGO == null)
                {
                    var go = new GameObject("BoatModWheelGuiOverlay");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    var canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 30000;

                    var panelGo = new GameObject("BoatModWheelGuiPanel", typeof(UnityEngine.UI.Image));
                    panelGo.transform.SetParent(go.transform, false);
                    var img = panelGo.GetComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0f, 0f, 0f, 0.7f);
                    var panelRect = panelGo.GetComponent<RectTransform>();
                    panelRect.anchorMin = new Vector2(0f, 1f);
                    panelRect.anchorMax = new Vector2(0f, 1f);
                    panelRect.pivot = new Vector2(0f, 1f);
                    panelRect.anchoredPosition = new Vector2(10f, -10f);
                    panelRect.sizeDelta = new Vector2(520f, 150f);

                    _uiCanvas = canvas;
                    _uiPanelGO = panelGo;
                    Log.LogInfo("[BoatMod] wheel: canvas panel created (uGUI)");
                }

                if (_tmpText == null)
                {
                    ResolveTmpFont();
                    var textGo = new GameObject("BoatModWheelGuiText", typeof(TMPro.TextMeshProUGUI));
                    textGo.transform.SetParent(_uiPanelGO.transform, false);
                    var tmp = textGo.GetComponent<TMPro.TextMeshProUGUI>();
                    tmp.font = _tmpFont;
                    tmp.fontSize = 20;
                    tmp.color = Color.white;
                    tmp.alignment = TMPro.TextAlignmentOptions.TopLeft;
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
                    var txtRect = tmp.rectTransform;
                    txtRect.anchorMin = new Vector2(0f, 0f);
                    txtRect.anchorMax = new Vector2(1f, 1f);
                    txtRect.offsetMin = new Vector2(10f, 6f);
                    txtRect.offsetMax = new Vector2(-10f, -6f);
                    _tmpText = tmp;
                    Log.LogInfo("[BoatMod] wheel: TMP overlay text created (font " + (_tmpFont != null ? _tmpFont.name : "NULL") + ")");
                }
            }
            catch (Exception e)
            {
                _uiTextFailed = true;
                Log.LogWarning($"[BoatMod] wheel: canvas overlay creation failed: {e.Message}");
            }
        }

        private static void SetOverlayActive(bool want)
        {
            try
            {
                if (_uiPanelGO != null) _uiPanelGO.gameObject.SetActive(want);
                if (_uiText != null) _uiText.gameObject.SetActive(want);
                if (_tmpText != null) _tmpText.gameObject.SetActive(want);
            }
            catch { }
        }

        private static void UpdateUiText()
        {
            bool want = _guiVisible || _calStep != 0;
            if (!want || _uiPanelGO == null) return;
            float now = Time.unscaledTime;
            if (now < _nextUiTextUpdate) return;
            _nextUiTextUpdate = now + 0.2f;
            try
            {
                var text = BuildOverlayText();
                if (_tmpText != null) _tmpText.text = text;
                if (_uiText != null) _uiText.text = text;
            }
            catch { }
        }

        private sealed class WheelGui : MonoBehaviour
        {
            public void OnGUI()
            {
                try
                {
                    if (!_guiFired)
                    {
                        _guiFired = true;
                        Log.LogInfo("[BoatMod] wheel: overlay OnGUI fired (IMGUI path works)");
                    }
                    if (_uiPanelGO != null) return;   // canvas renderer owns the visuals once it exists
                    if (!_guiVisible && _calStep == 0) return;
                    float top = _calStep != 0 ? 96f : 24f;
                    var box = new Rect(8f, 8f, 470f, 12f + top);
                    GUI.Box(box, GUIContent.none);
                    var label = new Rect(16f, 16f, 454f, box.height - 16f);
                    GUI.Label(label, BuildOverlayText());
                }
                catch { }
            }
        }
    }
}
