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
                return;
            }

            float dt = Time.unscaledTime - _lastFrame;
            _lastFrame = Time.unscaledTime;
            if (dt <= 0f || dt > 0.5f) dt = 0.016f;
            float smooth = Mathf.Clamp01(1f - Mathf.Exp(-dt / Mathf.Max(0.001f, _smoothing.Value)));

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

            if (BoatModPlugin.Instance != null && BoatModPlugin.Instance.Diag.Value && Time.unscaledTime - _nextDiagLog > 5f)
            {
                _nextDiagLog = Time.unscaledTime;
                Log.LogInfo($"[BoatMod] wheel: steer={_outSteer:F2} pedal={_outPedal:F2} raw(r={raw.x:F2} t={raw.y:F2} b={raw.z:F2}) ranges(t={_throttleRange:F2} b={_brakeRange:F2})");
            }
        }

        #region Steering calibration

        private static Key? ParseCalKey()
        {
            if (string.IsNullOrEmpty(_calKey?.Value) || _calKey.Value.Length != 1) return null;
            char c = char.ToUpperInvariant(_calKey.Value[0]);
            if (c >= 'A' && c <= 'Z')
                return (Key)(c - 'A' + 15); // Key enum: A=15 .. Z=40
            if (c >= '1' && c <= '9')
                return (Key)(c - '1' + 41); // Digit1=41 .. Digit9=49
            if (c == '0')
                return (Key)50;             // Digit0=50
            return null;
        }

        private static bool HandleCalibration(float rawSteer)
        {
            Key? key = ParseCalKey();
            if (key.HasValue)
            {
                try
                {
                    var kb = Keyboard.current;
                    var btn = kb != null ? kb[key.Value] : null;
                    if (btn != null && btn.wasPressedThisFrame)
                        NextCalStep();
                }
                catch (Exception e)
                {
                    if (!_calLoggedStep)
                    {
                        _calLoggedStep = true;
                        Log.LogWarning($"[BoatMod] wheel: cal key read failed: {e.Message}");
                    }
                }
            }

            if (_calStep == 0) return false;

            switch (_calStep)
            {
                case 1:
                    _calSum += rawSteer;
                    _calSamples++;
                    return false;
                case 2:
                    _calSum += rawSteer;
                    _calSamples++;
                    _calSteerLeft.Value = Math.Min(_calSteerLeft.Value, rawSteer);
                    return false;
                case 3:
                    _calSum += rawSteer;
                    _calSamples++;
                    _calSteerRight.Value = Math.Max(_calSteerRight.Value, rawSteer);
                    return false;
            }
            return false;
        }

        private static void NextCalStep()
        {
            float now = Time.unscaledTime;
            switch (_calStep)
            {
                case 0:
                    _calStep = 1;
                    _calStartedAt = now;
                    _calSum = 0f;
                    _calSamples = 0;
                    Log.LogInfo("[BoatMod] wheel: CALIBRATION started - step 1/3: HOLD WHEEL CENTERED, press key again when ready");
                    break;
                case 1:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: center sample too short ({_calSamples} frames), press key again"); return; }
                        _calSteerCenter.Value = _calSum / _calSamples;
                        _calStep = 2;
                        _calStartedAt = now;
                        _calSum = 0f;
                        _calSamples = 0;
                        _calSteerLeft.Value = _calSteerCenter.Value;
                        Log.LogInfo($"[BoatMod] wheel: center = {_calSteerCenter.Value:F3} - step 2/3: TURN WHEEL FULL LEFT AND HOLD, press key again");
                        break;
                    }
                case 2:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: left sample too short, press key again"); return; }
                        _calStep = 3;
                        _calStartedAt = now;
                        _calSum = 0f;
                        _calSamples = 0;
                        _calSteerRight.Value = _calSteerCenter.Value;
                        Log.LogInfo($"[BoatMod] wheel: left lock = {_calSteerLeft.Value:F3} (center {_calSteerCenter.Value:F3}) - step 3/3: TURN WHEEL FULL RIGHT AND HOLD, press key again");
                        break;
                    }
                case 3:
                    {
                        if (_calSamples < 10) { Log.LogWarning($"[BoatMod] wheel: right sample too short, press key again"); return; }
                        _calStep = 0;
                        if (Math.Abs(_calSteerLeft.Value - _calSteerCenter.Value) < 0.05f || Math.Abs(_calSteerRight.Value - _calSteerCenter.Value) < 0.05f)
                        {
                            Log.LogWarning("[BoatMod] wheel: calibration looks degenerate (locks too close to center), NOT saved");
                            break;
                        }
                        _calSteerSaved.Value = true;
                        _calStartedAt = 0f;
                        Log.LogInfo($"[BoatMod] wheel: CALIBRATION COMPLETE - center={_calSteerCenter.Value:F3} left={_calSteerLeft.Value:F3} right={_calSteerRight.Value:F3} (saved to config)");
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
    }
}
