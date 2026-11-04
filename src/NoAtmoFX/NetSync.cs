// Multiplayer: every player sees the same clouds.
// 1) Without any packet: the cloud field is driven by the mission clock that the game already shares
//    (MissionManager: NetworkTime - multiplayerStartTime) and by the weather SyncVars of LevelInfo
//    (conditions, cloudHeight, windVelocity, timeOfDay). Drift and evolution are integrated on that clock, so
//    clients that join late start where the others are. This works on any server, also dedicated ones without mods.
// 2) If the host also runs NoAtmoFX: it sends to the clients that have the mod (they say hello first) its cloud
//    settings, the exact drift/weather state and the nuclear holes still open, so late joiners get those too.
//    A server or client without the mod never receives one of these messages (hello costs it one 'unknown message').
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using Mirage;
using Mirage.Serialization;
using UnityEngine;

namespace NoAtmoFX
{
    public struct NoAtmoFXMsg { public byte kind; public byte[] data; }

    class NOCSync
    {
        const int PROTO = 1;
        const byte HELLO = 1, STATE = 2, CFG = 3, NUKE = 4;
        static readonly HashSet<string> SyncedSections = new HashSet<string> { "Weather", "Layer", "Shape", "Scale", "Variation", "Wind", "Storm", "Nuclear" };
        static readonly HashSet<string> NotSynced = new HashSet<string> { "Nuclear/UnlockNukes", "Nuclear/TestBlastKey", "Nuclear/TestYield", "Nuclear/TestDistance", "Nuclear/Enabled" };

        readonly NOCloudsPlugin _p;
        readonly ConfigFile _C;
        internal ConfigEntry<bool> On, FollowHost;

        // shared clock
        internal bool Shared;          // multiplayer with a mission clock: drive the clouds by it
        internal double T, DT;         // mission time (s) and its step this frame
        internal bool Jumped;          // the clock was reset this frame (mission start): drift restarts from it
        bool _clockInit; double _netT;
        internal bool IsHost, IsClientOnly;

        // networking
        MessageHandler _srvMH, _cliMH;
        NetworkServer _srv; NetworkClient _cli;
        readonly HashSet<INetworkPlayer> _modPlayers = new HashSet<INetworkPlayer>();
        bool _helloSent; int _hellos; float _helloT = -100, _stateT, _cfgCheckT; bool _saveFlag = true;
        internal bool HostHasMod; internal float LastHostMsg = -100;
        internal bool HostLive => Shared && IsClientOnly && HostHasMod && Time.time - LastHostMsg < 30f;
        // weather input other mods gave the host (integration API); replaces the client's own while the host is live
        internal NoAtmoFXApi.WeatherInput HostInput = NoAtmoFXApi.WeatherInput.None; internal bool HostInputValid;
        byte[] _lastInputSent;
        internal string Status = "";

        // host state to blend in (client)
        bool _haveHost, _snapNext;
        double _hT, _hWx, _hWz, _hEv; Vector3 _hWs;
        float _hCond, _hBase, _hHeat, _hH, _hI, _hFrontAmt; int _hFront;
        // host settings currently applied over ours (restored when leaving)
        readonly Dictionary<ConfigEntryBase, string> _saved = new Dictionary<ConfigEntryBase, string>();
        readonly StringBuilder _cfgIn = new StringBuilder();
        string _lastCfgSent = null;

        static bool _serializersSet;

        public NOCSync(NOCloudsPlugin p, ConfigFile C)
        {
            _p = p; _C = C;
            On = C.Bind("Multiplayer", "SyncClouds", true, "In multiplayer the clouds follow the mission clock shared by the game, so every player sees the same clouds (also on servers without mods). Off = old behaviour (each player's own drift).");
            FollowHost = C.Bind("Multiplayer", "FollowHostSettings", true, "When the host also runs NoAtmoFX: use the host's cloud settings (Weather, Layer, Shape, Scale, Variation, Wind, Storm, Nuclear) while in its game, so the clouds are identical. Your own settings come back when you leave. Lighting, quality, glass and atmosphere stay yours.");
            if (!_serializersSet)
            {
                _serializersSet = true;
                Writer<NoAtmoFXMsg>.Write = (w, m) => { w.WriteByte(m.kind); w.WriteBytesAndSize(m.data); };
                Reader<NoAtmoFXMsg>.Read = r => new NoAtmoFXMsg { kind = r.ReadByte(), data = r.ReadBytesAndSize(16384) };
            }
        }

        // ---------------- per frame, before the weather step ----------------
        public void Tick(float dt, bool levelReady)
        {
            var mm = NetworkSceneSingleton<MissionManager>.i;
            bool mp = On.Value && levelReady && mm != null && GameManager.gameState == GameState.Multiplayer;
            if (!mp)
            {
                if (Shared) Leave();
                Shared = false; _clockInit = false; DT = dt; Status = "";
                return;
            }
            // mission clock: the game's NetworkTime moves in small steps when it re-estimates the offset; follow it
            // smoothly so the clouds never jump (snap only if it is far off, e.g. right after joining)
            double net = 0;
            try { net = mm.NetworkTime.Time - mm.multiplayerStartTime; } catch { }
            _netT = net;
            Jumped = false;
            if (!_clockInit || Math.Abs(net - T) > 5.0) { Jumped = _clockInit; DT = 0; T = net; _clockInit = true; }
            else
            {
                double nt = T + dt;
                nt += (net - nt) * Math.Min(1.0, dt / 2.0);
                DT = Math.Max(0, nt - T); T = nt;
            }
            Shared = true;

            try { Network(mm); } catch (Exception e) { NOCloudsPlugin.Log.LogWarning("Multiplayer sync: " + e.Message); }
            ApplyHost(dt);
            bool hostLive = IsClientOnly && Time.time - LastHostMsg < 30f;
            Status = IsHost ? $"host, {_modPlayers.Count} client(s) with NoAtmoFX" : hostLive ? "following the host (NoAtmoFX)" : "mission clock (host without NoAtmoFX or not answered yet)";
        }

        void Leave()
        {
            RestoreSettings();
            _modPlayers.Clear(); _helloSent = false; HostInputValid = false; HostInput = NoAtmoFXApi.WeatherInput.None; _lastInputSent = null; _hellos = 0; _haveHost = false; HostHasMod = false; LastHostMsg = -100; _lastCfgSent = null;
        }

        // ---------------- networking ----------------
        void Network(MissionManager mm)
        {
            IsHost = mm.IsServer; IsClientOnly = mm.IsClient && !mm.IsServer;
            if (IsHost)
            {
                var s = mm.Server;
                if (s != _srv || (s != null && s.MessageHandler != _srvMH))
                {
                    _srv = s; _srvMH = s?.MessageHandler; _modPlayers.Clear();
                    _srvMH?.RegisterHandler<NoAtmoFXMsg>(OnServerMsg, true);
                }
                _modPlayers.RemoveWhere(pl => pl == null || !IsConnected(s, pl));
                if (_modPlayers.Count > 0 && Time.time - _stateT > 10f) { _stateT = Time.time; foreach (var pl in _modPlayers) SendState(pl, false); }
                if (Time.unscaledTime - _cfgCheckT > 1f)   // host changed a setting: send it (checked once a second)
                {
                    _cfgCheckT = Time.unscaledTime;
                    string cfg = HostSettings();
                    if (_lastCfgSent != null && cfg != _lastCfgSent) foreach (var pl in _modPlayers) SendSettings(pl, cfg);
                    _lastCfgSent = cfg;
                    // weather input from other mods changed: send the state now, not in up to 10 s
                    var ms = new MemoryStream(); NoAtmoFXApi.In.Write(new BinaryWriter(ms)); var cur = ms.ToArray();
                    if (_lastInputSent != null && !Same(cur, _lastInputSent)) { _stateT = Time.time; foreach (var pl in _modPlayers) SendState(pl, false); }
                    _lastInputSent = cur;
                }
            }
            else if (IsClientOnly)
            {
                var c = mm.Client;
                if (c != _cli || (c != null && c.MessageHandler != _cliMH))
                {
                    _cli = c; _cliMH = c?.MessageHandler; _helloSent = false; _hellos = 0;
                    _cliMH?.RegisterHandler<NoAtmoFXMsg>(OnClientMsg, true);
                }
                // hello once per connection, and once more a minute later if the host never answered
                if (c != null && c.IsConnected && _hellos < 2 && (!_helloSent || (!HostHasMod && Time.time - _helloT > 60f)))
                {
                    _helloSent = true; _helloT = Time.time; _hellos++;
                    c.Send(new NoAtmoFXMsg { kind = HELLO, data = BitConverter.GetBytes(PROTO) });
                }
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool IsConnected(NetworkServer s, INetworkPlayer pl)
        {
            if (s == null) return false;
            foreach (var x in s.AllPlayers) if (x == pl) return true;
            return false;
        }

        void OnServerMsg(INetworkPlayer pl, NoAtmoFXMsg m)
        {
            if (m.kind != HELLO || pl == null) return;
            _modPlayers.Add(pl);
            NOCloudsPlugin.Log.LogInfo($"Multiplayer: {pl} has NoAtmoFX, sending clouds.");
            SendSettings(pl, HostSettings());
            SendState(pl, true);
        }

        void OnClientMsg(INetworkPlayer pl, NoAtmoFXMsg m)
        {
            if (m.data == null) return;
            HostHasMod = true; LastHostMsg = Time.time;
            try
            {
                if (m.kind == STATE) ReadState(m.data);
                else if (m.kind == CFG) ReadSettings(m.data);
                else if (m.kind == NUKE) ReadNukes(m.data);
            }
            catch (Exception e) { NOCloudsPlugin.Log.LogWarning("Multiplayer: bad message from host: " + e.Message); }
        }

        // ---- host settings ----
        string HostSettings()
        {
            var sb = new StringBuilder();
            foreach (var kv in _C)
            {
                if (!Synced(kv.Key)) continue;
                var e = kv.Value;
                string v = e.GetSerializedValue();
                if (v == TomlDefault(e)) continue;   // only what differs from the defaults (missing = default)
                sb.Append(kv.Key.Section).Append('/').Append(kv.Key.Key).Append('=').Append(v).Append('\n');
            }
            return sb.ToString();
        }
        static string TomlDefault(ConfigEntryBase e) => TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
        static bool Synced(ConfigDefinition d) => SyncedSections.Contains(d.Section) && !NotSynced.Contains(d.Section + "/" + d.Key);

        void SendSettings(INetworkPlayer pl, string cfg)
        {
            byte[] all = Encoding.UTF8.GetBytes(cfg);
            const int CH = 900;
            int n = Math.Max(1, (all.Length + CH - 1) / CH);
            for (int i = 0; i < n; i++)
            {
                int len = Math.Min(CH, all.Length - i * CH);
                var ms = new MemoryStream(); var w = new BinaryWriter(ms);
                w.Write((byte)i); w.Write((byte)n); w.Write(all, i * CH, Math.Max(0, len));
                pl.Send(new NoAtmoFXMsg { kind = CFG, data = ms.ToArray() });
            }
        }

        void ReadSettings(byte[] d)
        {
            int i = d[0], n = d[1];
            if (i == 0) _cfgIn.Length = 0;
            _cfgIn.Append(Encoding.UTF8.GetString(d, 2, d.Length - 2));
            if (i != n - 1) return;
            if (!FollowHost.Value) { NOCloudsPlugin.Log.LogInfo("Multiplayer: host settings received, not used (FollowHostSettings off)."); return; }
            var host = new Dictionary<string, string>();
            foreach (var line in _cfgIn.ToString().Split('\n'))
            {
                int k = line.IndexOf('=');
                if (k > 0) host[line.Substring(0, k)] = line.Substring(k + 1);
            }
            if (_saved.Count == 0) _saveFlag = _C.SaveOnConfigSet;
            _C.SaveOnConfigSet = false;   // stays off while the host's values are in: the file keeps yours
            int changed = 0;
            try
            {
                foreach (var kv in _C)
                {
                    if (!Synced(kv.Key)) continue;
                    var e = kv.Value;
                    string want = host.TryGetValue(kv.Key.Section + "/" + kv.Key.Key, out var hv) ? hv : TomlDefault(e);
                    string cur = e.GetSerializedValue();
                    if (cur == want) continue;
                    if (!_saved.ContainsKey(e)) _saved[e] = cur;
                    e.SetSerializedValue(want); changed++;
                }
            }
            finally { if (_saved.Count == 0) _C.SaveOnConfigSet = _saveFlag; }
            NOCloudsPlugin.Log.LogInfo($"Multiplayer: using the host's cloud settings ({changed} changed, yours come back when you leave).");
        }

        internal bool UsingHostSettings => _saved.Count > 0;

        void RestoreSettings()
        {
            if (_saved.Count == 0) return;
            try { foreach (var kv in _saved) kv.Key.SetSerializedValue(kv.Value); }
            finally { _saved.Clear(); _C.SaveOnConfigSet = _saveFlag; }
            NOCloudsPlugin.Log.LogInfo("Multiplayer: your own cloud settings are back.");
        }

        // menu saves the file: write our own values, not the host's
        internal void BeforeSave(Action save)
        {
            if (_saved.Count == 0) { save(); return; }
            var cur = new Dictionary<ConfigEntryBase, string>();
            try
            {
                foreach (var kv in _saved) { cur[kv.Key] = kv.Key.GetSerializedValue(); kv.Key.SetSerializedValue(kv.Value); }
                save();
            }
            finally { foreach (var kv in cur) kv.Key.SetSerializedValue(kv.Value); }
        }

        // short code of the cloud settings, to compare between players when the host has no mod
        internal string SettingsCode()
        {
            uint h = 2166136261;
            foreach (char ch in HostSettings()) { h ^= ch; h *= 16777619; }
            return (h & 0xFFFF).ToString("X4");
        }

        // ---- drift / weather state ----
        void SendState(INetworkPlayer pl, bool full)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(full); w.Write(T);
            w.Write(_p._windX); w.Write(_p._windZ); w.Write(_p._evolveT);
            w.Write(_p._windS.x); w.Write(_p._windS.z);
            w.Write(_p._condS); w.Write(_p._baseS); w.Write(_p._heatS); w.Write(_p._Hs); w.Write(_p._Is); w.Write(_p._frontAmt); w.Write(_p._front);
            // appended in 1.3.0 (older clients stop reading before it): the host's integration-API weather input
            w.Write((byte)1); NoAtmoFXApi.In.Write(w);
            pl.Send(new NoAtmoFXMsg { kind = STATE, data = ms.ToArray() });
            if (full && _p.Nukes != null) pl.Send(new NoAtmoFXMsg { kind = NUKE, data = _p.Nukes.Pack(T, true) });
        }

        void ReadState(byte[] d)
        {
            var r = new BinaryReader(new MemoryStream(d));
            bool full = r.ReadBoolean();
            _hT = r.ReadDouble(); _hWx = r.ReadDouble(); _hWz = r.ReadDouble(); _hEv = r.ReadDouble();
            _hWs = new Vector3(r.ReadSingle(), 0, r.ReadSingle());
            _hCond = r.ReadSingle(); _hBase = r.ReadSingle(); _hHeat = r.ReadSingle(); _hH = r.ReadSingle(); _hI = r.ReadSingle(); _hFrontAmt = r.ReadSingle(); _hFront = r.ReadInt32();
            if (r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == 1) { HostInput = NoAtmoFXApi.WeatherInput.Read(r); HostInputValid = true; }
            else HostInputValid = false;   // host with an older NoAtmoFX
            _haveHost = true;
            if (full) _snapNext = true;
        }

        // the host's state, carried to our clock, replaces ours: at once right after joining, otherwise blended in
        // over ~10 s (the difference is tiny when everything works; the rule is that clouds never jump)
        void ApplyHost(float dt)
        {
            if (!_haveHost || !IsClientOnly || !_p._wxInit) return;
            double lag = T - _hT;   // host time of the message vs ours now (includes the travel time)
            double wx = _hWx + _hWs.x * lag, wz = _hWz + _hWs.z * lag, ev = _hEv + _p.EvolveRate * lag;
            float k = _snapNext ? 1f : 1f - Mathf.Exp(-dt / 10f);
            _p._windX += (wx - _p._windX) * k; _p._windZ += (wz - _p._windZ) * k; _p._evolveT += (ev - _p._evolveT) * k;
            _p._windS += (_hWs - _p._windS) * k;
            _p._condS += (_hCond - _p._condS) * k; _p._baseS += (_hBase - _p._baseS) * k; _p._heatS += (_hHeat - _p._heatS) * k;
            _p._Hs += (_hH - _p._Hs) * k; _p._Is += (_hI - _p._Is) * k;
            if (_snapNext || _p._front == _hFront) { _p._front = _hFront; _p._frontAmt += (_hFrontAmt - _p._frontAmt) * k; }
            if (_snapNext) NOCloudsPlugin.Log.LogInfo($"Multiplayer: clouds synced with the host (drift diff {Math.Sqrt((wx - _p._windX) * (wx - _p._windX) + (wz - _p._windZ) * (wz - _p._windZ)):F0} m before).");
            _snapNext = false;
            // the blend target moves with the clock, so keep carrying it
            _hT = T; _hWx = wx; _hWz = wz; _hEv = ev;
        }

        // ---- nukes ----
        internal void HostBlast(byte[] pack)
        {
            if (!Shared || !IsHost || _modPlayers.Count == 0) return;
            foreach (var pl in _modPlayers) pl.Send(new NoAtmoFXMsg { kind = NUKE, data = pack });
        }

        void ReadNukes(byte[] d) { _p.Nukes?.Unpack(d, T); }
    }
}
