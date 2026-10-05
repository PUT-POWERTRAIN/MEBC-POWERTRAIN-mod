using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BoatMod
{
    internal static class FossenPhysics
    {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _rudderGain;
        private static ConfigEntry<float> _propWash;
        private static ConfigEntry<float> _addedMass;
        private static ConfigEntry<float> _yawAddedMass;
        private static ConfigEntry<float> _swayDrag;
        private static ConfigEntry<float> _yawDrag;
        private static ConfigEntry<float> _coriolis;
        private static ConfigEntry<float> _lowSpeedAssist;
        private static ConfigEntry<float> _torqueTaper;
        private static ConfigEntry<string> _liveKey;

        private static bool _liveOn;
        private static bool _announced;
        private static bool _cacheOk;
        private static bool _cacheLogged;
        private static Harmony _harmony;
        private static float _nextCleanup;

        private static readonly Dictionary<int, BoatState> _states = new Dictionary<int, BoatState>();
        private static BoatState _lastMine;

        private static ManualLogSource Log => BoatModPlugin.Log;

        private class BoatState
        {
            public Component boat;
            public Component configComp;
            public Component floater;
            public Component battery;
            public Component motor;
            public Transform motorT;
            public Rigidbody rb;
            public bool broke;
            public bool hasPrev;
            public float vPrev, rPrev;
            public float u, v, r, delta, force, fy, n, throttle, bat;
        }

        private static PropertyInfo _pThrottle, _pAngle, _pExtra, _pForce, _pCurrent, _pFx, _pFy, _pView;
        private static FieldInfo _fBattery;
        private static PropertyInfo _cMaxReq, _cMaxForce, _cMaxAngle, _cEff, _cDragMult, _cUpwards, _cDepth, _cAngDrag;
        private static PropertyInfo _bRb, _bLv, _bBattery;
        private static PropertyInfo _fPoints, _fDepth;
        private static PropertyInfo _fpMult;
        private static PropertyInfo _batRate;
        private static MethodInfo _waveH, _discharge;
        private static Type _boatType, _configType;

        internal static void BindAndInit(ConfigFile cfg, Harmony harmony)
        {
            _enabled = cfg.Bind("Fossen", "Enabled", true, "3-DOF Fossen boat dynamics (surge stock, lateral+yaw Fossen). F10 toggles live, this is the startup default");
            _rudderGain = cfg.Bind("Fossen", "RudderGain", 25f, "Rudder lift: lateral force = sin(angle) * u^2 * RudderGain (stock uses SteeringStrength and kills it at full throttle; we do not)");
            _propWash = cfg.Bind("Fossen", "PropWashFactor", 0.35f, "Real rudder sits in the prop jet: extra turn authority at low speed while throttle is on");
            _addedMass = cfg.Bind("Fossen", "AddedMassFactor", 0.6f, "Water the hull drags along sideways, as fraction of boat mass (0 = arcade-tight)");
            _yawAddedMass = cfg.Bind("Fossen", "YawAddedMassFactor", 0.8f, "Rotational inertia of water around the hull, fraction of Izz");
            _swayDrag = cfg.Bind("Fossen", "SwayDrag", 1.5f, "Quadratic lateral hull drag: how fast sideways slide dies");
            _yawDrag = cfg.Bind("Fossen", "YawDrag", 1.2f, "Quadratic yaw damping: how fast the turn stops when you straighten the wheel");
            _coriolis = cfg.Bind("Fossen", "CoriolisFactor", 1f, "Fossen rotating-frame coupling: sway<-u*r and yaw<-(m22-m11)*u*v. 0 = off");
            _lowSpeedAssist = cfg.Bind("Fossen", "LowSpeedAssist", 0.4f, "Parked-turn assist: extra lateral thrust when nearly stopped with throttle on (outboard behavior)");
            _torqueTaper = cfg.Bind("Fossen", "TorqueTaper", 0.3f, "Fraction of stock angular drag kept from the floater (Fossen yaw damping covers the rest)");
            _liveKey = cfg.Bind("Fossen", "LiveKey", "F10", "Keyboard key that toggles Fossen on/off live");

            _liveOn = _enabled.Value;
            _harmony = harmony;
            BuildCache();
            if (!_cacheOk)
            {
                Log.LogWarning("[BoatMod] fossen: game types missing, physics swap disabled (stock physics everywhere)");
                return;
            }

            int patched = 0;
            foreach (var name in new[] { "Motor", "Floater" })
            {
                var t = BoatModPlugin.FindType(name);
                if (t == null) continue;
                var m = t.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null || m.DeclaringType != t) continue;
                var prefix = new HarmonyMethod(typeof(FossenPhysics), name == "Motor" ? nameof(MotorPrefix) : nameof(FloaterPrefix));
                _harmony.Patch(m, prefix: prefix);
                Log.LogInfo($"[BoatMod] fossen: hooked {name}.FixedUpdate");
                patched++;
            }
            if (patched < 2) Log.LogWarning($"[BoatMod] fossen: only {patched}/2 physics hooks installed");
        }

        private static void BuildCache()
        {
            try
            {
                _boatType = BoatModPlugin.FindType("Boat");
                _configType = BoatModPlugin.FindType("BoatConfiguration");
                var motorType = BoatModPlugin.FindType("Motor");
                var floaterType = BoatModPlugin.FindType("Floater");
                var fpType = BoatModPlugin.FindType("FloatingPoint");
                var waveType = BoatModPlugin.FindType("WaveManager");
                var batteryType = BoatModPlugin.FindType("Battery");
                if (_boatType == null || _configType == null || motorType == null || floaterType == null ||
                    fpType == null || waveType == null || batteryType == null) return;

                _fBattery = motorType.GetField("_battery", BindingFlags.Instance | BindingFlags.NonPublic);
                _pThrottle = motorType.GetProperty("Throttle");
                _pAngle = motorType.GetProperty("AngleScale");
                _pExtra = motorType.GetProperty("ExtraForce");
                _pForce = motorType.GetProperty("Force");
                _pCurrent = motorType.GetProperty("Current");
                _pFx = motorType.GetProperty("Fx");
                _pFy = motorType.GetProperty("Fy");
                _pView = motorType.GetProperty("photonView");

                _cMaxReq = _configType.GetProperty("MaxRequiredCurrent");
                _cMaxForce = _configType.GetProperty("MaxMotorForce");
                _cMaxAngle = _configType.GetProperty("MaxRudderAngle");
                _cEff = _configType.GetProperty("PropulsiveEfficiency");
                _cDragMult = _configType.GetProperty("LinearDragMultiplierOfHull");
                _cUpwards = _configType.GetProperty("UpwardsForceMultiplierOfHull");
                _cDepth = _configType.GetProperty("DepthBeforeSubmerged");
                _cAngDrag = _configType.GetProperty("AngularDragMultiplierOfHull");

                _bRb = _boatType.GetProperty("RigidBody");
                _bLv = _boatType.GetProperty("LocalVelocity");
                _bBattery = _boatType.GetProperty("Battery");

                _fPoints = floaterType.GetProperty("FloatingPoints");
                _fDepth = floaterType.GetProperty("DepthMultiplier");
                _fpMult = fpType.GetProperty("ForceMultiplier");
                _batRate = batteryType.GetProperty("CapacityRate");

                _waveH = waveType.GetMethod("GetWaveHeight", BindingFlags.Static | BindingFlags.Public);
                _discharge = batteryType.GetMethod("Discharge", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float), typeof(float) }, null);

                _cacheOk = _fBattery != null && _pThrottle != null && _pAngle != null && _pExtra != null &&
                           _pForce != null && _pCurrent != null && _pFx != null && _pFy != null && _pView != null &&
                           _cMaxReq != null && _cMaxForce != null && _cMaxAngle != null && _cEff != null &&
                           _cDragMult != null && _cUpwards != null && _cDepth != null && _cAngDrag != null &&
                           _bRb != null && _bLv != null && _bBattery != null &&
                           _fPoints != null && _fDepth != null && _fpMult != null &&
                           _waveH != null && _discharge != null;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] fossen: cache build failed: {e.Message}");
            }
            if (_cacheOk && !_cacheLogged)
            {
                _cacheLogged = true;
                Log.LogInfo("[BoatMod] fossen: reflection cache OK");
            }
        }

        internal static void Tick()
        {
            try
            {
                if (WheelInput.KeyDownNow(WheelInput.ParseNamedKey(_liveKey)))
                {
                    _liveOn = !_liveOn;
                    foreach (var s in _states.Values) s.hasPrev = false;
                    Log.LogInfo($"[BoatMod] fossen: {(_liveOn ? "ENABLED" : "DISABLED")} ({_liveKey.Value})");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] fossen: tick failed: {e.Message}");
            }
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextCleanup) return;
                _nextCleanup = now + 5f;
                var dead = new List<int>();
                foreach (var kv in _states)
                    if (kv.Value.boat == null || kv.Value.motor == null && kv.Value.floater == null) dead.Add(kv.Key);
                foreach (var k in dead) _states.Remove(k);
            }
            catch { }
        }

        internal static bool Active => _cacheOk && _liveOn;

        internal static string StatusLine()
        {
            if (!_cacheOk) return "Fossen: unavailable (game types missing)";
            if (!_liveOn) return "Fossen: off (stock physics, " + _liveKey.Value + " to enable)";
            var st = _lastMine;
            if (st == null || st.boat == null) return "Fossen: ON (no boat in water)";
            return $"Fossen: ON  u {st.u * 3.6f:F0}km/h  drift {st.v:F2}m/s  yaw {st.r * Mathf.Rad2Deg:F0}deg/s  rudder {st.delta * Mathf.Rad2Deg:F0}deg  thr {st.throttle * 100f:F0}%  bat {st.bat * 100f:F0}%";
        }

        private static bool IsMine(Component c)
        {
            try
            {
                var pv = _pView.GetValue(c, null);
                if (pv == null) return true;
                var mine = pv.GetType().GetProperty("IsMine");
                if (mine == null) return true;
                return Equals(true, mine.GetValue(pv, null));
            }
            catch { return true; }
        }

        private static BoatState StateForBoat(Component boatComp)
        {
            if (boatComp == null) return null;
            int id = boatComp.GetInstanceID();
            if (_states.TryGetValue(id, out var st) && st.boat != null) return st;
            if (st != null) _states.Remove(id);
            if (!_cacheOk) return null;
            try
            {
                st = new BoatState();
                st.boat = boatComp;
                st.configComp = boatComp.GetComponent(_configType);
                st.floater = boatComp.GetComponent(BoatModPlugin.FindType("Floater"));
                st.battery = _bBattery.GetValue(boatComp, null) as Component;
                st.rb = _bRb.GetValue(boatComp, null) as Rigidbody;
                if (st.configComp == null || st.rb == null || st.battery == null)
                {
                    Log.LogWarning($"[BoatMod] fossen: boat '{boatComp.name}' missing config/rigidbody/battery, stock physics");
                    return null;
                }
                _states[id] = st;
                return st;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] fossen: state build failed: {e.Message}");
                return null;
            }
        }

        private static bool MotorPrefix(object __instance)
        {
            if (!_liveOn) return true;
            var comp = __instance as Component;
            if (comp == null) return true;
            if (!IsMine(comp)) return true;
            try
            {
                var boatComp = comp.GetComponentInParent(_boatType) as Component;
                var st = StateForBoat(boatComp);
                if (st == null || st.broke) return true;
                st.motor = comp;
                st.motorT = comp.transform;
                ApplyMotor(st, comp);
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] fossen: motor pass failed ({e.Message}) - stock physics for this boat");
                if (_states.TryGetValue(comp.GetInstanceID(), out var st)) st.broke = true;
            }
            return false;
        }

        private static void ApplyMotor(BoatState st, Component motor)
        {
            float dt = Time.fixedDeltaTime;
            float throttle = (float)_pThrottle.GetValue(motor, null);
            float angleScale = (float)_pAngle.GetValue(motor, null);
            float extra = (float)_pExtra.GetValue(motor, null);

            float maxReq = (float)_cMaxReq.GetValue(st.configComp, null);
            float maxForce = (float)_cMaxForce.GetValue(st.configComp, null);
            float eff = (float)_cEff.GetValue(st.configComp, null);

            float req = Mathf.Lerp(-maxReq, maxReq, throttle);
            float current = (float)_discharge.Invoke(st.battery, new object[] { Math.Abs(req), dt });
            if (req < 0f) current = -current;
            float frac = Mathf.InverseLerp(-maxReq, maxReq, current);
            float force = Mathf.Lerp(-maxForce, maxForce, frac);

            float f = st.motorT.localEulerAngles.y * Mathf.Deg2Rad;
            float sf = Mathf.Sin(f), cf = Mathf.Cos(f);

            float fx = cf * force * eff;
            st.rb.AddRelativeForce(0f, 0f, fx * extra, ForceMode.Force);

            Vector3 lv = (Vector3)_bLv.GetValue(st.boat, null);
            float u = lv.z;
            float vNow = lv.x;
            float rNow = st.rb.angularVelocity.y;

            if (float.IsNaN(u + vNow + rNow) || float.IsInfinity(u + vNow + rNow))
            {
                st.hasPrev = false;
                return;
            }

            float m = st.rb.mass;
            float izz = st.rb.inertiaTensor.y;
            if (float.IsNaN(izz) || izz < 1f) izz = m * 2f;
            float m22 = m * (1f + _addedMass.Value);
            float m66 = izz * (1f + _yawAddedMass.Value);

            float fy = sf * force * eff;
            fy += sf * u * Mathf.Abs(u) * _rudderGain.Value;

            float uAbs = Mathf.Abs(u);
            float uRef = 3f;
            if (uAbs < uRef && Mathf.Abs(force) > 1f)
                fy += sf * Mathf.Abs(force) * eff * _propWash.Value * (1f - uAbs / uRef);
            if (uAbs < 0.3f && Mathf.Abs(throttle) > 0.05f)
                fy += sf * Mathf.Abs(force) * eff * _lowSpeedAssist.Value;

            fy -= m * u * rNow * _coriolis.Value;

            float vClamp = Mathf.Clamp(vNow, -6f, 6f);
            fy -= m22 * (0.15f * vClamp + 0.25f * _swayDrag.Value * vClamp * Mathf.Abs(vClamp));

            if (st.hasPrev)
            {
                float aSway = Mathf.Clamp((vNow - st.vPrev) / dt, -8f, 8f);
                fy -= (m22 - m) * aSway;
            }
            fy = Mathf.Clamp(fy, -3f * maxForce, 3f * maxForce);
            _pFx.SetValue(motor, fx);
            _pFy.SetValue(motor, fy);
            _pForce.SetValue(motor, force);
            _pCurrent.SetValue(motor, current);

            st.rb.AddForceAtPosition(st.rb.transform.right * fy, st.motorT.position, ForceMode.Force);

            float n = 0f;
            if (st.hasPrev)
            {
                float aYaw = Mathf.Clamp((rNow - st.rPrev) / dt, -10f, 10f);
                n -= (m66 - izz) * aYaw;
            }
            float rClamp = Mathf.Clamp(rNow, -4f, 4f);
            n -= m66 * (0.3f * rClamp + 0.25f * _yawDrag.Value * rClamp * Mathf.Abs(rClamp));
            n += (m22 - m) * u * vNow * _coriolis.Value;
            n = Mathf.Clamp(n, -izz * 10f, izz * 10f);
            st.rb.AddTorque(0f, n, 0f, ForceMode.Force);

            st.hasPrev = true;
            st.vPrev = vNow;
            st.rPrev = rNow;
            st.u = u;
            st.v = vNow;
            st.r = rNow;
            st.delta = f;
            st.force = force;
            st.fy = fy;
            st.n = n;
            st.throttle = throttle;
            try { st.bat = (float)_batRate.GetValue(st.battery, null); } catch { }
            _lastMine = st;

            if (!_announced)
            {
                _announced = true;
                Log.LogInfo($"[BoatMod] fossen: ACTIVE on '{st.boat.name}' (mass {m:F0}kg, izz {izz:F0}, m22 {m22:F0}, m66 {m66:F0}) - stock lateral physics replaced");
            }
        }

        private static bool FloaterPrefix(object __instance)
        {
            if (!_liveOn) return true;
            var comp = __instance as Component;
            if (comp == null) return true;
            if (!IsMine(comp)) return true;
            try
            {
                var boatComp = comp.GetComponent(_boatType) as Component;
                if (boatComp == null) boatComp = comp.GetComponentInParent(_boatType) as Component;
                var st = StateForBoat(boatComp);
                if (st == null || st.broke) return true;
                ApplyFloater(st, comp);
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] fossen: floater pass failed ({e.Message}) - stock physics for this boat");
                if (_states.TryGetValue(comp.GetInstanceID(), out var st)) st.broke = true;
            }
            return false;
        }

        private static void ApplyFloater(BoatState st, Component floater)
        {
            var rb = st.rb;
            Vector3 lv = (Vector3)_bLv.GetValue(st.boat, null);
            Vector3 vector = -lv.magnitude * lv.magnitude * lv.normalized;
            Vector3 dragMult = (Vector3)_cDragMult.GetValue(st.configComp, null);
            vector.x = 0f;
            vector.y *= dragMult.y;
            vector.z *= dragMult.z;
            if (float.IsNaN(vector.x + vector.y + vector.z)) vector = Vector3.zero;

            var points = _fPoints.GetValue(floater, null) as Array;
            int n = points != null ? points.Length : 0;
            if (n == 0) return;

            float num = 0f;
            float angDrag = (float)_cAngDrag.GetValue(st.configComp, null) * _torqueTaper.Value;
            for (int i = 0; i < n; i++)
            {
                var fp = points.GetValue(i) as Component;
                if (fp == null) continue;
                Vector3 pos = fp.transform.position;
                float waveH = (float)_waveH.Invoke(null, new object[] { pos });
                rb.AddForceAtPosition(Physics.gravity / n, pos, ForceMode.Acceleration);
                if (pos.y < waveH)
                {
                    float depth = (float)_cDepth.GetValue(st.configComp, null);
                    float up = (float)_cUpwards.GetValue(st.configComp, null);
                    float mult = (float)_fpMult.GetValue(fp, null);
                    float num2 = Mathf.Clamp01((waveH - pos.y) / depth) * up * mult;
                    num += num2;
                    rb.AddForceAtPosition(Vector3.up * (-Physics.gravity.y) * num2 / n, pos, ForceMode.Acceleration);
                    rb.AddForce(num2 * floater.transform.TransformVector(vector) * Time.fixedDeltaTime, ForceMode.Acceleration);
                    rb.AddTorque(num2 * -rb.angularVelocity * angDrag * Time.fixedDeltaTime, ForceMode.Acceleration);
                }
            }
            _fDepth.SetValue(st.floater, num / n);
        }
    }
}
