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
        private static ConfigEntry<string> _calKey;

        private static ConfigEntry<bool> _calSteerSaved;
        private static ConfigEntry<float> _calSteerCenter, _calSteerLeft, _calSteerRight;

        private static Harmony _harmony;
        private static readonly Dictionary<Type, FieldInfo> _motorInputFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> _inputEnabledFields = new Dictionary<Type, FieldInfo>();

        private static Joystick _device;
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

        private static GameObject _guiGO;
        private static float _nextGuiRecreate;
        private static bool _guiLoggedAlive;

        private static readonly float[] _ring = new float[64];
        private static int _ringIdx, _ringCount;

        private static ConfigEntry<bool> _diag;
        private static ConfigEntry<string> _guiKey;
        private static bool _guiVisible;

        private static float _smSteer, _smThrottle, _smBrake;
        private static float _lastFrame;
        private static float _outSteer, _outPedal;
        private static bool _injectOn;
        private static float _nextDiagLog;
        private static bool _tickLogged;
        private static int _lastPumpedFrame = -1;
        private static bool _calLoggedStep;

        private static ManualLogSource Log => BoatModPlugin.Log;

        internal static void BindAndInit(ConfigFile cfg, Harmony harmony)
        {
            BindConfig(cfg);
            if (_calSteerSaved.Value && Math.Abs(_calSteerCenter.Value) > 0.35f)
                Log.LogWarning($"[BoatMod] wheel: saved steering center {_calSteerCenter.Value:F3} looks implausible (>0.35) - boat will steer weirdly until recalibrated (press {_calKey.Value})");
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
                    _harmony.Patch(m, prefix: new HarmonyMethod(typeof(WheelInput), nameof(MotorInputPrefix)));
                    BoatModPlugin.Log.LogInfo($"[BoatMod] wheel: hooked {name}.FixedUpdate");
                    patched++;
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
            if (!_tickLogged)
            {
                _tickLogged = true;
                int devCount = -1;
                try { devCount = InputSystem.devices.Count; } catch (Exception e) { Log.LogWarning($"[BoatMod] wheel: devices probe failed: {e.Message}"); }
                Log.LogInfo($"[BoatMod] wheel: Tick alive, enabled={_enabled?.Value.ToString() ?? "null"}, InputSystem.devices={devCount}");
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
                Log.LogInfo($"[BoatMod] wheel: device '{_device.displayName}' description='{_device.description.product}'");
                return;
            }

            if (!_captured)
            {
                CaptureRest();
                return;
            }

            Vector3 raw;
            try
            {
                raw = new Vector3(
                    _steer != null ? _steer.ReadValue() : 0f,
                    _throttle != null ? _throttle.ReadValue() : 0f,
                    _brake != null ? _brake.ReadValue() : 0f);
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
            float smooth = Mathf.Clamp01(1f - Mathf.Exp(-dt / Mathf.Max(0.001f, _smoothing.Value)));

            _ring[_ringIdx] = raw.x;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;

            if (HandleCalibration(raw.x)) return;

            float throttleRaw = Remap(raw.y, _restThrottle, ref _throttleRange, symmetric: false);
            float brakeRaw = Remap(raw.z, _restBrake, ref _brakeRange, symmetric: false);
            if (_throttleInv.Value) throttleRaw = -throttleRaw;
            if (_brakeInv.Value) brakeRaw = -brakeRaw;
            throttleRaw = ApplyDeadzone(throttleRaw, _pedalDeadzone.Value);
            brakeRaw = ApplyDeadzone(brakeRaw, _pedalDeadzone.Value);

            float steerRaw = _calStep != 0 ? 0f : SteerFromCalibration(raw.x);
            if (_steerInv.Value) steerRaw = -steerRaw;

            _smSteer += (steerRaw - _smSteer) * smooth;
            _smThrottle += (throttleRaw - _smThrottle) * smooth;
            _smBrake += (brakeRaw - _smBrake) * smooth;

            _outSteer = Mathf.Clamp(_smSteer, -1f, 1f);
            _outPedal = Mathf.Clamp(_smThrottle - _smBrake, -1f, 1f);
            _injectOn = true;

            if (_diag != null && _diag.Value && Time.unscaledTime - _nextDiagLog > 5f)
            {
                _nextDiagLog = Time.unscaledTime;
                Log.LogInfo($"[BoatMod] wheel: steer={_outSteer:F2} pedal={_outPedal:F2} raw(r={raw.x:F2} t={raw.y:F2} b={raw.z:F2}) ranges(t={_throttleRange:F2} b={_brakeRange:F2})");
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
                string stepName = _calStep == 1 ? "center-hold" : _calStep == 2 ? "left-hold" : "right-hold";
                Log.LogInfo($"[BoatMod] wheel: cal ({stepName}) live raw={rawSteer:F3}");
            }

            switch (_calStep)
            {
                case 1:
                    _calSum += rawSteer;
                    _calSamples++;
                    return false;
                case 2:
                    _calSum += rawSteer;
                    _calSamples++;
                    if (rawSteer < _wipLeft) _wipLeft = rawSteer;
                    return false;
                case 3:
                    _calSum += rawSteer;
                    _calSamples++;
                    if (rawSteer > _wipRight) _wipRight = rawSteer;
                    return false;
            }
            return false;
        }

        private static float RingRecent(int n)
        {
            n = Math.Min(Math.Min(n, _ringCount), _ring.Length);
            if (n <= 0) return 0f;
            float s = 0f;
            for (int i = 0; i < n; i++) s += _ring[(_ringIdx - 1 - i + _ring.Length * 2) % _ring.Length];
            return s / n;
        }

        private static float RingRecentSpread(int n)
        {
            n = Math.Min(Math.Min(n, _ringCount), _ring.Length);
            if (n <= 0) return 0f;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float v = _ring[(_ringIdx - 1 - i + _ring.Length * 2) % _ring.Length];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            return hi - lo;
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
                    Log.LogInfo("[BoatMod] wheel: CALIBRATION started - step 1/3: HOLD WHEEL CENTERED, press key again when ready");
                    break;
                case 1:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: center sample too short ({_calSamples} frames), press key again"); return; }
                        float recent = RingRecent(20);
                        float spread = RingRecentSpread(20);
                        _wipCenter = recent;
                        _calStep = 2;
                        _calStartedAt = now;
                        _calSum = 0f;
                        _calSamples = 0;
                        _wipLeft = _wipCenter;
                        Log.LogInfo($"[BoatMod] wheel: center = {_wipCenter:F3} (recent-window of 20, spread {spread:F3}{(spread > 0.2f ? " WARNING: wheel moving during hold" : "")}) - step 2/3: TURN WHEEL FULL LEFT AND HOLD, press key again");
                        break;
                    }
                case 2:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: left sample too short, press key again"); return; }
                        _calStep = 3;
                        _calStartedAt = now;
                        _calSum = 0f;
                        _calSamples = 0;
                        _wipRight = _wipCenter;
                        Log.LogInfo($"[BoatMod] wheel: left lock = {_wipLeft:F3} (center {_wipCenter:F3}) - step 3/3: TURN WHEEL FULL RIGHT AND HOLD, press key again");
                        break;
                    }
                case 3:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: right sample too short, press key again"); return; }
                        _calStep = 0;
                        _calStartedAt = 0f;
                        if (Math.Abs(_wipLeft - _wipCenter) < 0.05f || Math.Abs(_wipRight - _wipCenter) < 0.05f)
                        {
                            Log.LogWarning("[BoatMod] wheel: calibration looks degenerate (locks too close to center), NOT saved - previous calibration kept: " + CalStatusLine());
                            break;
                        }
                        _calSteerCenter.Value = _wipCenter;
                        _calSteerLeft.Value = _wipLeft;
                        _calSteerRight.Value = _wipRight;
                        _calSteerSaved.Value = true;
                        Log.LogInfo($"[BoatMod] wheel: CALIBRATION COMPLETE - center={_wipCenter:F3} left={_wipLeft:F3} right={_wipRight:F3} (saved to config)");
                        break;
                    }
            }
        }

        private static float SteerFromCalibration(float raw)
        {
            if (!_calSteerSaved.Value)
                return 0f;
            float center = _calSteerCenter.Value;
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
            outv = Mathf.Clamp(outv, -1f, 1f);
            return ApplyDeadzone(outv, _steerDeadzone.Value);
        }

        #endregion

        private static void MotorInputPrefix(object __instance)
        {
            if (!_injectOn) return;
            try
            {
                var t = __instance.GetType();
                if (!t.Name.Contains("BoatInput")) return;
                if (!_motorInputFields.TryGetValue(t, out var field))
                {
                    field = t.GetField("_motorInput", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _motorInputFields[t] = field;
                }
                if (field == null) return;
                if (!_inputEnabledFields.TryGetValue(t, out var enableField))
                {
                    enableField = t.GetField("IsInputEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _inputEnabledFields[t] = enableField;
                }
                if (enableField != null && !Equals(true, enableField.GetValue(__instance))) return;
                var current = (Vector2)field.GetValue(__instance);
                var merged = new Vector2(
                    Mathf.Abs(_outSteer) > 0.001f ? _outSteer : current.x,
                    Mathf.Abs(_outPedal) > 0.001f ? _outPedal : current.y);
                field.SetValue(__instance, merged);
            }
            catch { }
        }

        internal static void SetDiag(ConfigEntry<bool> diag)
        {
            _diag = diag;
        }

        private static void ResolveControls(Joystick dev)
        {
            _steer = dev.TryGetChildControl<AxisControl>(_steerCtl.Value);
            _throttle = dev.TryGetChildControl<AxisControl>(_throttleCtl.Value);
            _brake = dev.TryGetChildControl<AxisControl>(_brakeCtl.Value);
            if (_steer == null || _throttle == null || _brake == null)
            {
                var missing = new List<string>();
                if (_steer == null) missing.Add(_steerCtl.Value);
                if (_throttle == null) missing.Add(_throttleCtl.Value);
                if (_brake == null) missing.Add(_brakeCtl.Value);
                Log.LogWarning($"[BoatMod] wheel: missing control(s) [{string.Join(", ", missing)}] on '{dev.displayName}'");
            }
            DumpControls(dev);
        }

        private static void DumpControls(Joystick dev)
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

        private static Joystick FindDevice()
        {
            try
            {
                var match = _deviceName.Value;
                foreach (var d in Joystick.all)
                {
                    if (string.IsNullOrEmpty(match) ||
                        (d.displayName ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (d.description.product ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d;
                }
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
                if (_calSteerSaved.Value)
                    Log.LogInfo($"[BoatMod] wheel: using saved steering calibration center={_calSteerCenter.Value:F3} left={_calSteerLeft.Value:F3} right={_calSteerRight.Value:F3} (press {_calKey.Value} to recalibrate)");
                else
                    Log.LogInfo($"[BoatMod] wheel: no saved steering calibration - run it (press {_calKey.Value} to start, see log steps). pedals rest t={_restThrottle:F2} b={_restBrake:F2}");
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
            _enabled = cfg.Bind("Wheel", "Enabled", true, "Drive the boat with the MOZA R3 wheel/pedals (master switch)");
            _deviceName = cfg.Bind("Wheel", "DeviceName", "Gudsen", "Substring match for the joystick device (empty = first joystick found)");
            _steerCtl = cfg.Bind("Wheel", "SteerControl", "stick/x", "Wheel axis control path (see wheel control dump in the log)");
            _throttleCtl = cfg.Bind("Wheel", "ThrottleControl", "z", "Throttle pedal axis control name");
            _brakeCtl = cfg.Bind("Wheel", "BrakeControl", "rz", "Brake pedal axis control name");
            _steerInv = cfg.Bind("Wheel", "SteerInvert", false, "Invert steering direction");
            _throttleInv = cfg.Bind("Wheel", "ThrottleInvert", false, "Invert throttle pedal direction");
            _brakeInv = cfg.Bind("Wheel", "BrakeInvert", false, "Invert brake pedal direction");
            _steerDeadzone = cfg.Bind("Wheel", "SteerDeadzone", 0.02f, "Deadzone at wheel center (0..1)");
            _pedalDeadzone = cfg.Bind("Wheel", "PedalDeadzone", 0.05f, "Deadzone for pedals (0..1)");
            _smoothing = cfg.Bind("Wheel", "SmoothingSeconds", 0.15f, "Smoothing window in seconds (lower = snappier)");
            _calKey = cfg.Bind("Wheel", "CalKey", "K", "Keyboard key that advances the steering calibration wizard (A-Z, 0-9)");
            _guiKey = cfg.Bind("Wheel", "DebugKey", "F3", "Keyboard key that toggles the wheel debug overlay (show during calibration regardless)");

            _calSteerSaved = cfg.Bind("Wheel", "SteerCalibrated", false, "Internal: steering calibration present");
            _calSteerCenter = cfg.Bind("Wheel", "SteerCenter", 0f, "Internal: calibrated steering center (raw value)");
            _calSteerLeft = cfg.Bind("Wheel", "SteerLeft", 0f, "Internal: full LEFT lock raw value");
            _calSteerRight = cfg.Bind("Wheel", "SteerRight", 0f, "Internal: full RIGHT lock raw value");
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

        private static string CalStatusLine()
        {
            if (!_calSteerSaved.Value)
                return "not calibrated - press " + (_calKey?.Value ?? "K") + " to run wizard";
            return $"cal: center {_calSteerCenter.Value:F3}  L {_calSteerLeft.Value:F3}  R {_calSteerRight.Value:F3}";
        }

        private static string CalStepText()
        {
            switch (_calStep)
            {
                case 1: return "STEP 1/3 - HOLD WHEEL CENTERED, press " + (_calKey?.Value ?? "K") + " again when stable";
                case 2: return "STEP 2/3 - TURN WHEEL FULL LEFT AND HOLD, press " + (_calKey?.Value ?? "K") + " when there";
                case 3: return "STEP 3/3 - TURN WHEEL FULL RIGHT AND HOLD, press " + (_calKey?.Value ?? "K") + " when there";
                default: return null;
            }
        }

        private static string BuildOverlayText()
        {
            var lines = new List<string>
            {
                "[F3] toggle  --  wheel: " + (_device == null ? "(no device yet)" : _device.displayName),
                CalStatusLine(),
            };
            if (_calStep != 0)
            {
                lines.Add(CalStepText());
                lines.Add("cal live raw = " + (_device != null && _steer != null ? _steer.ReadValue().ToString("F3") : "?"));
            }
            else
            {
                lines.Add($"out: steer {_outSteer:F2}  pedal {_outPedal:F2}  inject={(_injectOn ? "on" : "off")}");
                if (_device != null && _steer != null && _throttle != null && _brake != null)
                    lines.Add($"raw: r {_steer.ReadValue():F3}  t {_throttle.ReadValue():F3}  b {_brake.ReadValue():F3}");
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
