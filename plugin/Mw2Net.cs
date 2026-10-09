using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// Private multiplayer (Phase 2) over RoR2's own UNET connection. RoR2 already syncs bodies,
    /// movement and damage (MW2 shots / blasts are RoR2 BulletAttacks / BlastAttacks); this sends
    /// what MW2 draws locally so friends see it too:
    ///  - player state (20 Hz): weapon, stance, sprint, ADS, aim, move, fire / reload / melee / throw
    ///    -> each remote player is drawn as an MW2 soldier with their gun, flashes, tracers, sounds;
    ///  - props (15 Hz): every MW2 model the owner has spawned (killstreak aircraft, crates, sentries,
    ///    grenades, rockets) -> mirrored as visuals (damage stays with the owner);
    ///  - one-shot effects and world sounds (explosions, impacts, announcer...);
    ///  - hello: plugin build + native ABI, so mismatched installs are called out.
    /// The server relays those to everyone but their sender (the host included). Gameplay that
    /// RoR2 keeps on the host goes through it instead:
    ///  - kill (host -> all): RoR2 raises deaths on the host only; each client counts its own kills
    ///    (XP, killstreaks, challenges, Scavenger) from it (its own death it sees itself);
    ///  - class (client -> host): the client's perk items (inventories are the host's) and its
    ///    deathstreak, so the host applies Painkiller / Final Stand / Last Stand / Martyrdom to it;
    ///  - stand (host -> all) / stand end (client -> host): a client's last stand starts on the
    ///    host's damage, its timer runs on the client, the host gets it up or bleeds it out.
    static class Mw2Net
    {
        const short MsgId = 0x4D32; // "M2"
        const byte KPlayer = 1, KProps = 2, KFx = 3, KSound = 4, KHello = 5, KKill = 6, KClass = 7, KStand = 8, KStandEnd = 9, KStreak = 10, KStreakPick = 11, KStreakDrop = 12, KTacIns = 13, KTeam = 14, KCrate = 15, KVehHit = 16, KLobby = 17;

        class Msg : MessageBase
        {
            public byte kind;
            public byte[] data;
            public override void Serialize(NetworkWriter w) { w.Write(kind); w.WriteBytesAndSize(data, data.Length); }
            public override void Deserialize(NetworkReader r) { kind = r.ReadByte(); data = r.ReadBytesAndSize(); }
        }

        static NetworkClient registeredClient;
        static bool serverRegistered;
        static float nextPlayer, nextProps;
        // The zip's name plus the compile's id: readable in the mismatch message, still unique per build.
        static readonly string build = Plugin.Version + " #" + typeof(Mw2Net).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 6);
        static bool helloSent;
        static Mw2Bridge bridge;

        public static bool Online => NetworkClient.active || NetworkServer.active;

        /// Solo test of the whole path: the server sends your own messages back and you're drawn
        /// as a "remote" ghost 2.5 m to your right (props too).
        public static bool Echo;
        const uint EchoBit = 0x80000000u;
        static Vector3 EchoOffset(CharacterBody b)
        {
            var cam = Camera.main;
            var right = cam != null ? Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized : Vector3.right;
            var fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : Vector3.forward;
            return b != null ? right * 0.9f : Vector3.zero; // beside you at the same depth, in view
        }

        public static void Init(Mw2Bridge b)
        {
            bridge = b;
            Mw2Fx.Broadcast = (name, at, fwd) => { if (bridge.LocalBody2 != null && !Throttled(name)) SendFx(KFx, name, at, fwd); };
            Mw2Killstreaks.SoundBroadcast = (alias, at) => { if (bridge.LocalBody2 != null && !Throttled(alias)) SendFx(KSound, alias, at, Vector3.zero); };
            // A sound's own volume goes along in the (unused) direction: the sentry's quiet fire played
            // at full volume on everyone else's PC (10-06-26).
            Mw2Killstreaks.SoundBroadcastVol = (alias, at, vol) => { if (bridge.LocalBody2 != null && !Throttled(alias)) SendFx(KSound, alias, at, new Vector3(Mathf.Max(vol, 0.001f), 0f, 0f)); };
        }

        // One of each effect / sound per 40 ms is enough for the others (a shotgun's 12 impacts, a
        // minigun's rounds); the rest would only flood the connection.
        static readonly Dictionary<string, float> lastSent = new Dictionary<string, float>();
        static bool Throttled(string name)
        {
            float now = Time.unscaledTime;
            if (lastSent.TryGetValue(name, out float t) && now - t < 0.04f) return true;
            lastSent[name] = now;
            return false;
        }

        // RoR2's channels (QosChannelIndex): 0 reliable, 1 unreliable, 7 effects. Player state and
        // props are full state at 20 / 15 Hz - a lost one is replaced by the next, and on the
        // reliable channel a lag spike would queue them up (or overflow it). Unreliable packets
        // don't fragment, so a big props update stays reliable.
        static int ChannelFor(byte kind, int bytes)
        {
            if (kind == KPlayer) return 1;
            if (kind == KProps) return bytes <= 1000 ? 1 : 0;
            if (kind == KFx || kind == KSound) return 7;
            return 0;
        }

        /// Once per frame.
        public static void Update()
        {
            Register();
            var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
            bool connected = client != null && client.isConnected;
            if (!connected) { helloSent = helloAnswered = false; ClearRemotes(); return; }
            if (!helloSent) { helloSent = true; SendHello(); }
            LobbyUpdate();
            var body = bridge.LocalBody2;
            if (body != null && Time.unscaledTime >= nextPlayer) { nextPlayer = Time.unscaledTime + 0.05f; SendPlayer(body); }
            if (body != null && Time.unscaledTime >= nextProps) { nextProps = Time.unscaledTime + 1f / 15f; SendProps(body); }
            // A client repeats its class every 5 s (the first may beat the host's setup of its body);
            // repeats are harmless: the host only arms Final Stand from a body's first.
            if (!NetworkServer.active && body != null && lastClass != null && lastClassBody == body && Time.unscaledTime >= nextClass) SendClass(body, lastClass);
            if (NetworkServer.active && Time.unscaledTime >= nextStatusSweep) { nextStatusSweep = Time.unscaledTime + 10f; SweepStatuses(); }
            StepRemotes();
        }

        static void Register()
        {
            if (NetworkServer.active && !NetworkServer.handlers.ContainsKey(MsgId))
            {
                NetworkServer.RegisterHandler(MsgId, OnServer);
                serverRegistered = true;
            }
            if (!NetworkServer.active) serverRegistered = false;
            var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
            if (client != null && (client != registeredClient || !client.handlers.ContainsKey(MsgId)))
            {
                client.RegisterHandler(MsgId, OnClient);
                registeredClient = client;
            }
        }

        // ------------------------------------------------------------------ send / relay

        static void Send(byte kind, byte[] data)
        {
            var client = NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
            if (client == null || !client.isConnected || client.connection == null) return;
            client.connection.SendByChannel(MsgId, new Msg { kind = kind, data = data }, ChannelFor(kind, data.Length));
        }

        /// Host -> every client (the host's own client ignores these: it acted on them already).
        static void SendToClients(byte kind, byte[] data)
        {
            if (!NetworkServer.active) return;
            var msg = new Msg { kind = kind, data = data };
            foreach (var c in NetworkServer.connections)
                if (c != null && c.isReady) c.SendByChannel(MsgId, msg, 0);
        }

        static void OnServer(NetworkMessage m)
        {
            var msg = m.ReadMessage<Msg>();
            // Kill and stand messages only ever come from the host: a client's would be forged.
            if (msg.kind == KKill || msg.kind == KStand || msg.kind == KStreakPick || msg.kind == KVehHit) return;
            if (msg.kind == KClass || msg.kind == KStandEnd || msg.kind == KStreak || msg.kind == KStreakDrop || msg.kind == KTacIns)
            {
                try
                {
                    var r = new NetworkReader(msg.data);
                    if (msg.kind == KClass) ServerClass(m.conn, r);
                    else if (msg.kind == KStandEnd) ServerStandEnd(m.conn, r);
                    else if (msg.kind == KStreakDrop) ServerStreakDrop(m.conn, r);
                    else if (msg.kind == KTacIns) ServerTacticalInsertion(m.conn, r);
                    else ServerStreak(m.conn, r);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[net] bad message {msg.kind}: {e.Message}"); }
                return;
            }
            int channel = ChannelFor(msg.kind, msg.data != null ? msg.data.Length : 0);
            foreach (var c in NetworkServer.connections)
                if (c != null && (c != m.conn || Echo) && c.isReady) c.SendByChannel(MsgId, msg, channel);
        }

        static void OnClient(NetworkMessage m)
        {
            var msg = m.ReadMessage<Msg>();
            try
            {
                var r = new NetworkReader(msg.data);
                switch (msg.kind)
                {
                    case KPlayer: ReadPlayer(r); break;
                    case KProps: ReadProps(r); break;
                    case KFx: ReadFx(r, false); break;
                    case KSound: ReadFx(r, true); break;
                    case KHello: ReadHello(r); break;
                    case KKill: ReadKill(r); break;
                    case KStand: ReadStand(r); break;
                    case KStreakPick: ReadStreakPick(r); break;
                    case KTeam: ReadTeam(r); break;
                    case KCrate: ReadCrate(r); break;
                    case KVehHit: ReadVehicleHit(r); break;
                    case KLobby: ReadLobby(r); break;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[net] bad message {msg.kind}: {e.Message}"); }
        }

        /// Does this MW2 soldier carry a riot shield, held (front) or on his back? The host asks this
        /// for every hit (Mw2Shield); teammates' come from their player messages.
        public static bool ShieldOf(CharacterBody b, out bool held, out bool back)
        {
            held = back = false;
            if (b == null) return false;
            if (b == bridge.LocalBody2) { held = bridge.ShieldHeld; back = bridge.ShieldBack; return true; }
            if (!remotes.TryGetValue(NetId(b), out var rm)) return false;
            held = Mw2Shield.Is(rm.weapon);
            back = rm.shieldBack;
            return true;
        }

        /// A player's MW2 stance (0 stand, 1 crouch, 2 prone, 3 last stand): this machine's own,
        /// or from a teammate's player messages.
        public static byte StanceOf(CharacterBody b)
        {
            if (b == null) return 0;
            if (b == bridge.LocalBody2) return bridge.CharacterStance;
            return remotes.TryGetValue(NetId(b), out var rm) ? rm.input.stance : (byte)0;
        }

        /// RoR2 sends every damage message to every player: a teammate's MW2 soldier takes its pain /
        /// death conditions from the hit here, as his own machine does (Mw2Character.HitFrom).
        public static void OnDamage(DamageDealtMessage msg)
        {
            var victim = msg != null && msg.victim != null ? msg.victim.GetComponent<CharacterBody>() : null;
            if (victim == null) return;
            uint id = NetId(victim);
            if (victim == bridge.LocalBody2) { if (!Echo) return; id |= EchoBit; } // echo: our own mirrored soldier
            if (!remotes.TryGetValue(id, out var rm)) return;
            rm.input.hit = Mw2Character.HitFrom(victim, Space.DirToUnity(Dir(rm.pitch, rm.yaw)), msg);
        }

#if MW2_DEV
        /// Pilot: the echo soldier's last hit (0 = none / no echo).
        public static uint EchoHit()
        {
            var me = bridge.LocalBody2;
            if (me == null || !remotes.TryGetValue(NetId(me) | EchoBit, out var rm)) { Plugin.Log.LogInfo($"[pilot] no echo soldier ({remotes.Count} remotes)"); return 0u; }
            return rm.input.hit;
        }
#endif

        static uint NetId(CharacterBody b)
        {
            var id = b != null ? b.GetComponent<NetworkIdentity>() : null;
            return id != null ? id.netId.Value : 0u;
        }

        static CharacterBody Find(uint netId)
        {
            var go = ClientScene.FindLocalObject(new NetworkInstanceId(netId));
            return go != null ? go.GetComponent<CharacterBody>() : null;
        }

        static CharacterBody ServerFind(uint netId)
        {
            var go = NetworkServer.FindLocalObject(new NetworkInstanceId(netId));
            return go != null ? go.GetComponent<CharacterBody>() : null;
        }

        /// Does `conn` play `body`? (A client may only set up its own.)
        static bool Owns(NetworkConnection conn, CharacterBody body)
        {
            if (conn == null || body == null) return false;
            var id = body.GetComponent<NetworkIdentity>();
            if (id != null && id.clientAuthorityOwner == conn) return true;
            var user = body.master != null && body.master.playerCharacterMasterController != null ? body.master.playerCharacterMasterController.networkUser : null;
            return user != null && user.connectionToClient == conn;
        }

        // ------------------------------------------------------------------ kills / deaths

        /// Host: RoR2's death event. A client's Martyrdom goes off here; players hear about it.
        public static void ServerDeath(CharacterBody attacker, CharacterBody victim)
        {
            if (victim == null) return;
            uint victimId = NetId(victim);
            if (statuses.TryGetValue(victimId, out var st))
            {
                if (st.deathstreak == "specialty_grenadepulldeath") Mw2Deathstreaks.Martyrdom(victim);
                statuses.Remove(victimId);
            }
            if (!victim.isPlayerControlled && (attacker == null || !attacker.isPlayerControlled)) return;
            var w = new NetworkWriter();
            w.Write(NetId(attacker)); w.Write(victimId); w.Write(victim.corePosition);
            w.Write(Mw2Progress.KillKind(victim)); // monster / elite / boss: the kill's XP
            SendToClients(KKill, w.ToArray());
        }

        static void ReadKill(NetworkReader r)
        {
            uint attacker = r.ReadUInt32(), victim = r.ReadUInt32();
            var at = r.ReadVector3();
            byte kind = r.ReadByte();
            if (NetworkServer.active) return;
            var me = bridge.LocalBody2;
            if (me == null) return;
            uint mine = NetId(me);
            if (mine != 0 && attacker == mine && victim != mine) bridge.OnNetKill(at, kind);
        }

        // ------------------------------------------------------------------ class / deathstreaks

        /// What the host knows of a client's MW2 player (by body).
        public class Status
        {
            public string deathstreak = "";
            public float painkillerUntil = -1f, standUntil = -1f;
            public bool finalStandArmed;
        }
        static readonly Dictionary<uint, Status> statuses = new Dictionary<uint, Status>();

        /// Host: a client's MW2 status, or null (not a client's MW2 player).
        public static Status StatusOf(CharacterBody body)
        {
            if (body == null || statuses.Count == 0) return null;
            return statuses.TryGetValue(NetId(body), out var s) ? s : null;
        }

        static List<string> lastClass;
        static CharacterBody lastClassBody;
        static float nextClass, nextStatusSweep;

        /// Host: forget bodies that are gone (stage changes, runs).
        static void SweepStatuses()
        {
            List<uint> gone = null;
            foreach (var id in statuses.Keys) if (ServerFind(id) == null) (gone = gone ?? new List<uint>()).Add(id);
            if (gone != null) foreach (var id in gone) statuses.Remove(id);
        }

        /// Client: ask the host for these perk items and tell it the deathstreak in play.
        public static void SendClass(CharacterBody body, ICollection<string> perks)
        {
            if (body == null) return;
            if (!ReferenceEquals(perks, lastClass)) lastClass = new List<string>(perks);
            lastClassBody = body;
            nextClass = Time.unscaledTime + 5f;
            var w = new NetworkWriter();
            w.Write(NetId(body));
            w.Write((byte)Math.Min(perks.Count, 32));
            int n = 0;
            foreach (var p in perks) { if (n++ >= 32) break; w.Write(p); }
            w.Write(Mw2Deathstreaks.Active ?? "");
            w.Write(Mw2Deathstreaks.PainkillerLeft);
            w.Write(Mw2Deathstreaks.FinalStandArmed);
            Send(KClass, w.ToArray());
        }

        static void ServerClass(NetworkConnection conn, NetworkReader r)
        {
            uint id = r.ReadUInt32();
            int n = r.ReadByte();
            var perks = new List<string>();
            for (int i = 0; i < n; i++) perks.Add(r.ReadString());
            string deathstreak = r.ReadString();
            float painkillerLeft = r.ReadSingle();
            bool finalStand = r.ReadBoolean();
            var body = ServerFind(id);
            if (body == null) return;
            if (!Owns(conn, body)) { Plugin.Log.LogWarning($"[net] class for body {id} from a connection that doesn't own it: ignored"); return; }
            bool first = !statuses.TryGetValue(id, out var st);
            if (first) statuses[id] = st = new Status();
            st.deathstreak = deathstreak;
            st.painkillerUntil = painkillerLeft > 0f ? Time.time + painkillerLeft : -1f;
            // Final Stand is armed once per body (the host spends it; a later repeat mustn't re-arm).
            if (first) st.finalStandArmed = finalStand;
            Mw2Perks.Give(body, perks);
            Plugin.Log.LogInfo($"[net] client body {id}: perks {string.Join(", ", perks)}; deathstreak '{deathstreak}'");
        }

        /// Host: a client's body went into last stand (`getsUp`: Final Stand).
        public static void ServerStand(CharacterBody body, bool getsUp)
        {
            var w = new NetworkWriter();
            w.Write(NetId(body)); w.Write(getsUp);
            SendToClients(KStand, w.ToArray());
        }

        static void ReadStand(NetworkReader r)
        {
            uint id = r.ReadUInt32();
            bool up = r.ReadBoolean();
            if (NetworkServer.active) return;
            var me = bridge.LocalBody2;
            if (me != null && NetId(me) == id) Mw2Deathstreaks.StandFromHost(bridge, up);
        }

        /// Client: the last stand timer ran out - get up (Final Stand) or bleed out.
        public static void SendStandEnd(CharacterBody body, bool getsUp)
        {
            if (body == null) return;
            var w = new NetworkWriter();
            w.Write(NetId(body)); w.Write(getsUp);
            Send(KStandEnd, w.ToArray());
        }

        static void ServerStandEnd(NetworkConnection conn, NetworkReader r)
        {
            uint id = r.ReadUInt32();
            bool up = r.ReadBoolean();
            var body = ServerFind(id);
            if (body == null || body.healthComponent == null || !Owns(conn, body)) return;
            // Only a stand the host started (else "get up" would be a free full heal).
            if (!statuses.TryGetValue(id, out var st) || Time.time >= st.standUntil) return;
            st.standUntil = -1f;
            var hc = body.healthComponent;
            if (!hc.alive) return;
            if (up) hc.Networkhealth = hc.fullHealth;
            else hc.Suicide();
        }

        /// A killstreak that works for the whole team (MW2: a UAV shows enemies on every teammate's
        /// radar): to everyone else, relayed by the host; the sender has it already.
        public const byte TeamUav = 1, TeamNuke = 2;
        public static void SendTeam(byte what, float seconds)
        {
            var w = new NetworkWriter();
            w.Write(what); w.Write(seconds);
            Send(KTeam, w.ToArray());
        }

        /// A care package crate landed (`landed`) or was taken / timed out, for every teammate.
        public static void SendCrate(bool landed, uint id, Vector3 at, uint contents)
        {
            var w = new NetworkWriter();
            w.Write(landed); w.Write(id); w.Write(at); w.Write(contents);
            Send(KCrate, w.ToArray());
        }

        static void ReadCrate(NetworkReader r)
        {
            bool landed = r.ReadBoolean();
            uint id = r.ReadUInt32();
            var at = r.ReadVector3();
            uint contents = r.ReadUInt32();
            if (landed) bridge.Streaks.CrateFromTeam(id, at, contents);
            else bridge.Streaks.CrateGone(id);
        }

        static void ReadTeam(NetworkReader r)
        {
            byte what = r.ReadByte();
            float seconds = r.ReadSingle();
            bridge.Streaks.TeamStreak(what, seconds);
        }

        /// Client: a killstreak effect only the host may apply (StreakHost: EMP, nuke, shellshock).
        public static void SendStreak(CharacterBody body, byte what, Vector3 at, float a = 0f, float b = 0f)
        {
            if (body == null) return;
            var w = new NetworkWriter();
            w.Write(NetId(body)); w.Write(what); w.Write(at); w.Write(a); w.Write(b);
            Send(KStreak, w.ToArray());
        }

        static void ServerStreak(NetworkConnection conn, NetworkReader r)
        {
            uint id = r.ReadUInt32();
            byte what = r.ReadByte();
            var at = r.ReadVector3();
            float a = r.ReadSingle(), b = r.ReadSingle();
            var body = ServerFind(id);
            if (body == null || !Owns(conn, body)) return;
            StreakHost.Apply(what, body, at, a, b);
        }

        // ------------------------------------------------------------------ killstreak pickups

        /// Host: drops each client may still make (one per pickup handed to it).
        static readonly Dictionary<uint, int> dropCredits = new Dictionary<uint, int>();

        /// Host: a killstreak pickup reached `body` - its player swaps it in (Mw2Killstreaks.PickUp).
        public static void ServerStreakPickup(CharacterBody body, uint streak)
        {
            var bridge = Plugin.Instance.Bridge;
            var user = body.master != null && body.master.playerCharacterMasterController != null ? body.master.playerCharacterMasterController.networkUser : null;
            if (user != null && user.isLocalPlayer)
            {
                // The host's own player; not in MW2 mode (or no streaks yet): it goes back on the ground.
                if (body != bridge.LocalBody2 || !bridge.Streaks.PickUp(streak)) Mw2StreakItems.ServerDrop(body, streak);
                return;
            }
            uint id = NetId(body);
            dropCredits.TryGetValue(id, out int c);
            dropCredits[id] = c + 1;
            var w = new NetworkWriter();
            w.Write(id); w.Write(streak);
            SendToClients(KStreakPick, w.ToArray());
        }

        static void ReadStreakPick(NetworkReader r)
        {
            uint id = r.ReadUInt32(), streak = r.ReadUInt32();
            if (NetworkServer.active) return;
            // Ours even when MW2 mode isn't on right now (then it can't be taken: back on the ground).
            var me = bridge.LocalBody;
            if (me == null || NetId(me) != id) return;
            if (bridge.LocalBody2 != me || !bridge.Streaks.PickUp(streak)) DropStreak(me, streak);
        }

        /// A Tactical Insertion went down: the host keeps it to revive the player on (Mw2TacticalInsertion).
        public static void TacticalInsertion(CharacterBody body, Vector3 at)
        {
            if (NetworkServer.active || !Online) { Mw2TacticalInsertion.ServerPlant(body, at); return; }
            var w = new NetworkWriter();
            w.Write(NetId(body)); w.Write(at);
            Send(KTacIns, w.ToArray());
        }

        static void ServerTacticalInsertion(NetworkConnection conn, NetworkReader r)
        {
            uint id = r.ReadUInt32();
            var at = r.ReadVector3();
            var body = ServerFind(id);
            // A client sets up only its own; and it has to be near them (no planting across the map).
            // (a thrown flare can land well below a ledge he's standing on, and he keeps moving)
            if (!Owns(conn, body) || Vector3.Distance(body.footPosition, at) > 25f) return;
            Mw2TacticalInsertion.ServerPlant(body, at);
        }

        /// The streak a pickup replaced drops at the player's feet (the host makes the droplet).
        public static void DropStreak(CharacterBody body, uint streak)
        {
            if (NetworkServer.active) { Mw2StreakItems.ServerDrop(body, streak); return; }
            var w = new NetworkWriter();
            w.Write(NetId(body)); w.Write(streak);
            Send(KStreakDrop, w.ToArray());
        }

        static void ServerStreakDrop(NetworkConnection conn, NetworkReader r)
        {
            uint id = r.ReadUInt32(), streak = r.ReadUInt32();
            var body = ServerFind(id);
            if (body == null || !Owns(conn, body)) return;
            if (!dropCredits.TryGetValue(id, out int c) || c <= 0) return; // only after a pickup
            dropCredits[id] = c - 1;
            Mw2StreakItems.ServerDrop(body, streak);
        }

        // ------------------------------------------------------------------ hello

        static void SendHello()
        {
            var w = new NetworkWriter();
            w.Write(build); w.Write(Native.ExpectedAbi);
            // The MW2 items (perks, killstreak pickups) come from each player's MW2 install: both
            // sides need the same set or RoR2's item catalog differs between them.
            w.Write(Mw2Perks.Items.Count); w.Write(Mw2StreakItems.Items.Count);
            Send(KHello, w.ToArray());
            Plugin.Log.LogInfo($"[net] hello sent: build {build}, ABI {Native.ExpectedAbi}");
        }

        static void ReadHello(NetworkReader r)
        {
            string theirs = r.ReadString(); uint abi = r.ReadUInt32();
            bool same = theirs == build && abi == Native.ExpectedAbi;
            int perks = same ? r.ReadInt32() : -1, streakItems = same ? r.ReadInt32() : -1;
            bool items = !same || (perks == Mw2Perks.Items.Count && streakItems == Mw2StreakItems.Items.Count);
            Plugin.Log.LogInfo($"[net] peer build {theirs} ABI {abi}: {(same ? "match" : "MISMATCH")}; items {perks}/{streakItems} vs ours {Mw2Perks.Items.Count}/{Mw2StreakItems.Items.Count}");
            if (!same) Chat.AddMessage($"<color=#ff6060>MW2 mod mismatch: a player runs build {theirs} (ABI {abi}), you run {build} (ABI {Native.ExpectedAbi}). Everyone needs the same version of the mod.</color>");
            else if (!items) Chat.AddMessage($"<color=#ff6060>MW2 data mismatch: a player's MW2 install gave {perks} perks / {streakItems} streak items, yours {Mw2Perks.Items.Count} / {Mw2StreakItems.Items.Count}. Check everyone has MW2 installed and the mod found it.</color>");
            else if (!helloAnswered) { helloAnswered = true; SendHello(); } // late joiners hear back
        }
        static bool helloAnswered;

        // ------------------------------------------------------------------ player state

        static void SendPlayer(CharacterBody body)
        {
            var s = bridge.Last;
            var w = new NetworkWriter();
            w.Write(NetId(body));
            w.Write(bridge.SkinKey ?? Plugin.Instance.BodyFaction.Value ?? "us_army");
            w.Write(bridge.WeaponName ?? "");
            // The held gun's camo (mp/camoTable id; 0 none) for teammates' view of it.
            w.Write((byte)(Native.CamoIds.TryGetValue(s.weapon, out uint camo) ? Math.Min(camo, 254u) : 0u));
            // The item the body animates with when it isn't the gun (a throw, a killstreak laptop).
            uint anim = bridge.AnimWeapon;
            w.Write(anim != 0 ? Native.WeaponString(anim, 2) : "");
            w.Write(bridge.CharacterStance);
            w.Write((byte)((s.sprinting != 0 ? 1 : 0) | (s.grounded == 0 ? 2 : 0) | (bridge.ShieldBack ? 4 : 0)));
            w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(s.adsFrac) * 255f));
            w.Write(s.viewangles.x); w.Write(s.viewangles.y);
            float yr = s.viewangles.y * Mathf.Deg2Rad;
            w.Write(s.velocity.x * Mathf.Cos(yr) + s.velocity.y * Mathf.Sin(yr));
            w.Write(s.velocity.x * Mathf.Sin(yr) - s.velocity.y * Mathf.Cos(yr));
            w.Write((byte)bridge.TakeNetEvents());
            // Their rank's icon and number, for the badge over their head on everyone else's screen.
            w.Write(bridge.Progress.RankIcon ?? ""); w.Write(bridge.Progress.RankDisplay ?? "");
            Send(KPlayer, w.ToArray());
        }

        class Remote
        {
            public Mw2Character ch = new Mw2Character();
            public string faction, weaponName;
            public uint weapon;
            public Mw2CharacterInput input;
            public float pitch, yaw, seen;
            public byte pendingEvents;
            public int shotHand; // akimbo: last muzzle used
            public bool shieldBack; // riot shield stowed on his back
            public CharacterModel hidden;
            public bool tried;
            public string rankIcon, rankText;
        }
        static readonly Dictionary<uint, Remote> remotes = new Dictionary<uint, Remote>();
        static float nextRemoteLog;

        static void ReadPlayer(NetworkReader r)
        {
            uint id = r.ReadUInt32();
            string faction = r.ReadString(), weaponName = r.ReadString();
            byte camo = r.ReadByte();
            string animName = r.ReadString();
            byte stance = r.ReadByte(), flags = r.ReadByte(), ads = r.ReadByte();
            float pitch = r.ReadSingle(), yaw = r.ReadSingle(), fwd = r.ReadSingle(), right = r.ReadSingle();
            byte events = r.ReadByte();
            string rankIcon = r.ReadString(), rankText = r.ReadString();
            var local = bridge.LocalBody2;
            if (local != null && NetId(local) == id) { if (!Echo) return; id |= EchoBit; }
            if (!remotes.TryGetValue(id, out var rm)) remotes[id] = rm = new Remote();
            if (rm.faction != null && rm.faction != faction) { rm.ch.Destroy(); rm.tried = false; }
            rm.faction = faction;
            if (rm.weaponName != weaponName) { rm.weaponName = weaponName; rm.weapon = Native.WeaponIndex(weaponName); }
            rm.pitch = pitch; rm.yaw = yaw; rm.seen = Time.unscaledTime;
            rm.input.stance = stance;
            rm.input.sprinting = (byte)(flags & 1);
            rm.input.inAir = (byte)((flags >> 1) & 1);
            rm.shieldBack = (flags & 4) != 0;
            rm.input.adsFrac = ads / 255f;
            rm.input.aimPitch = pitch;
            rm.input.moveFwd = fwd; rm.input.moveRight = right;
            rm.input.weapon = rm.weapon;
            rm.ch.Camo = camo == 0 ? 255u : camo; // explicit: never this machine's own camo setting
            rm.ch.AnimWeapon = animName.Length > 0 ? Native.WeaponIndex(animName) : 0u;
            rm.pendingEvents |= events;
            rm.rankIcon = rankIcon; rm.rankText = rankText;
        }

        /// Teammates' MW2 rank badges over their heads (within 80 m, on screen).
        public static void DrawTeamRanks(Camera cam)
        {
            if (cam == null) return;
            foreach (var kv in remotes)
            {
                var rm = kv.Value;
                if (string.IsNullOrEmpty(rm.rankIcon)) continue;
                var body = Find(kv.Key & ~EchoBit);
                if (body == null || body.healthComponent == null || !body.healthComponent.alive) continue;
                var top = body.corePosition + Vector3.up * (body.radius * 2.6f + 0.6f);
                var sp = cam.WorldToScreenPoint(top);
                if (sp.z <= 0f || sp.z > 80f) continue;
                float s = Mathf.Round(Screen.height * 0.022f);
                Mw2Icons.Draw(new Rect(sp.x - s / 2f, Screen.height - sp.y - s, s, s), rm.rankIcon, Color.white);
            }
        }

        static void StepRemotes()
        {
            List<uint> gone = null;
            foreach (var kv in remotes)
            {
                var rm = kv.Value;
                var body = Find(kv.Key & ~EchoBit);
                var offset = (kv.Key & EchoBit) != 0 ? EchoOffset(body) : Vector3.zero;
                if (body == null || Time.unscaledTime - rm.seen > 5f) { (gone = gone ?? new List<uint>()).Add(kv.Key); continue; }
                if (!rm.ch.Exists && !rm.tried)
                {
                    rm.tried = true;
                    if (rm.ch.Build(body, rm.faction))
                    {
                        var model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
                        rm.hidden = model != null ? model.GetComponent<CharacterModel>() : null;
                        if (rm.hidden != null) rm.hidden.invisibilityCount++;
                        Mw2ItemDisplays.Bind(rm.hidden, rm.ch); // their RoR2 items on their MW2 body
                        Plugin.Log.LogInfo($"[net] remote player {kv.Key}: MW2 {rm.faction} body");
                    }
                }
                if (!rm.ch.Exists) continue;
                rm.input.dt = Time.deltaTime;
                rm.input.dead = (byte)(body.healthComponent != null && !body.healthComponent.alive ? 1 : 0);
                rm.input.events = rm.pendingEvents;
                var dir = Space.DirToUnity(Dir(rm.pitch, rm.yaw));
                var flat = new Vector3(dir.x, 0f, dir.z);
                float yawUnity = flat.sqrMagnitude > 1e-6f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : 0f;
                rm.ch.StowedShield = rm.shieldBack ? Mw2Shield.Index : 0u;
                rm.ch.Step(body.footPosition + offset, yawUnity, ref rm.input, true);
                if (Time.unscaledTime > nextRemoteLog)
                {
                    nextRemoteLog = Time.unscaledTime + 2f;
                    var root = rm.ch.Root;
                    var cam = Camera.main;
                    var mine = bridge.LocalCharacterRoot;
                    string Vp(Transform t) => t != null && cam != null ? cam.WorldToViewportPoint(t.position + Vector3.up).ToString("F2") : "-";
                    Plugin.Log.LogInfo($"[net] remote {kv.Key}: root {(root != null ? root.position.ToString("F2") + " active " + root.gameObject.activeInHierarchy : "null")} viewport {Vp(root)} | local viewport {Vp(mine)} active {(mine != null && mine.gameObject.activeInHierarchy)} | weapon {rm.weaponName}");
                }
                if ((rm.pendingEvents & Mw2Character.EvFire) != 0 && rm.weapon != 0) RemoteShot(rm, body, dir);
                rm.pendingEvents = 0;
            }
            if (gone != null) foreach (var id in gone) DropRemote(id);
        }

        static Vec3f Dir(float pitch, float yaw)
        {
            float p = pitch * Mathf.Deg2Rad, y = yaw * Mathf.Deg2Rad;
            return new Vec3f(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), -Mathf.Sin(p));
        }

        /// Someone else fired: MW2's world flash + eject at their gun, a tracer to what the shot
        /// would hit, and their weapon's third-person fire sound.
        static void RemoteShot(Remote rm, CharacterBody body, Vector3 dir)
        {
            // Akimbo: the hands take turns (the event byte doesn't say which fired).
            int hand = Native.mw2_weapon_is_akimbo(rm.weapon) == 1 ? (rm.shotHand ^= 1) : 0;
            var muzzle = rm.ch.Muzzle(out var gunFwd, hand) ?? (body.aimOrigin + dir * 0.6f);
            var end = muzzle + dir * 200f;
            if (Physics.Raycast(muzzle, dir, out var hit, 200f, LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore)) end = hit.point;
            Mw2Fx.NoBroadcast++;
            try { Mw2Gunfire.Shot(rm.weapon, muzzle, muzzle, dir, end, true, false, null, world: true); }
            finally { Mw2Fx.NoBroadcast--; }
            string alias = Native.WeaponString(rm.weapon, 5);
            if (!string.IsNullOrEmpty(alias)) PlaySound(alias, muzzle, 1f);
        }

        static void DropRemote(uint id)
        {
            if (!remotes.TryGetValue(id, out var rm)) return;
            Mw2ItemDisplays.Unbind(rm.hidden);
            rm.ch.Destroy();
            if (rm.hidden != null) rm.hidden.invisibilityCount--;
            remotes.Remove(id);
            ClearProps(id);
        }

        static void ClearRemotes()
        {
            foreach (var id in new List<uint>(remotes.Keys)) DropRemote(id);
            foreach (var id in new List<uint>(props.Keys)) ClearProps(id);
        }

        // ------------------------------------------------------------------ props

        static void SendProps(CharacterBody body)
        {
            Mw2Prop.Spawned.RemoveAll(l => l.go == null);
            var w = new NetworkWriter();
            w.Write(NetId(body));
            int count = 0;
            foreach (var l in Mw2Prop.Spawned) if (Shareable(l)) count++;
            w.Write((ushort)count);
            foreach (var l in Mw2Prop.Spawned)
            {
                if (!Shareable(l)) continue;
                var t = l.go.transform;
                w.Write(l.go.GetInstanceID());
                w.Write(l.model);
                w.Write(l.trail ?? "");
                w.Write(t.position); w.Write(t.rotation); w.Write(t.lossyScale.x);
                // Shootable aircraft: MW2 health and hitbox radius (the host gives it a hitbox).
                float health = Mw2Vehicle.SharedHealth(l.go, out float radius);
                w.Write(health); w.Write(radius);
                // Its moving parts: a turret's turn and pitch, the sentry's barrel spin (sent as the
                // root alone, a teammate's sentry sat frozen on everyone else's screen - 10-06-26).
                var tu = l.go.GetComponent<Mw2Turret>();
                bool turret = tu != null && tu.yawT != null && tu.pitchT != null;
                w.Write(turret);
                if (turret) { w.Write(tu.yawT.localRotation); w.Write(tu.pitchT.localRotation); }
                float spin = 0f;
                foreach (var sp in l.go.GetComponentsInChildren<Mw2Spin>()) if (sp.name.EndsWith("_spin")) spin = sp.speed;
                w.Write(spin);
            }
            if (count > 0 || lastSentProps > 0) Send(KProps, w.ToArray());
            lastSentProps = count;
        }
        static int lastSentProps;

        static bool Shareable(Mw2Prop.Live l) => l.go != null && l.go.activeInHierarchy && l.go.layer != Mw2View.Layer;

        class PropView { public GameObject go; public string model; public string trail; public uint fx; public Vector3 pos; public Quaternion rot; public bool seen; public GameObject hitbox; public RemoteAircraft aircraft; public bool turret; public Quaternion yaw = Quaternion.identity, pitch = Quaternion.identity; }

        /// A teammate's aircraft as the host's hitbox sees it: its hits go back to the owner.
        class RemoteAircraft : IShootable
        {
            public uint owner; public int id;
            bool killed; float sentFrac = 1f, sentAt;
            public bool Crashing => killed;
            public bool Dead => killed;
            public void Kill() { if (killed) return; killed = true; SendVehicleHit(owner, id, 0f, true); }
            public void Damaged(float frac)
            {
                if (killed || (sentFrac - frac < 0.01f && Time.unscaledTime - sentAt < 0.25f)) return;
                sentFrac = frac; sentAt = Time.unscaledTime;
                SendVehicleHit(owner, id, frac, false);
            }
        }

        static void SendVehicleHit(uint owner, int id, float frac, bool kill)
        {
            var w = new NetworkWriter();
            w.Write(owner); w.Write(id); w.Write(frac); w.Write(kill);
            SendToClients(KVehHit, w.ToArray());
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[net] aircraft hit -> owner {owner:x8} prop {id}: health {frac:F2}{(kill ? ", shot down" : "")}");
        }

        static void ReadVehicleHit(NetworkReader r)
        {
            uint owner = r.ReadUInt32(); int id = r.ReadInt32(); float frac = r.ReadSingle(); bool kill = r.ReadBoolean();
            var me = bridge.LocalBody2;
            if (me == null || (owner & ~EchoBit) != NetId(me) || ((owner & EchoBit) != 0 && !Echo)) return;
            Mw2Vehicle.RemoteHit(id, frac, kill);
        }

        // ------------------------------------------------------------------ character select
        // Everyone's soldier on character select holds their class's primary and wears their head, as
        // RoR2 shows teammates' survivors and loadouts (playtest 10-04-26). Sent on a change and every 2 s
        // (late joiners); keyed by the NetworkUser's net id.

        /// This player's display soldier's rolled head (-1 until one is built).
        public static int LocalLobbyHead = -1;
        static readonly Dictionary<uint, (string primary, int head)> lobby = new Dictionary<uint, (string, int)>();
        static string sentPrimary;
        static int sentHead = -2;
        static float nextLobby;

        static void LobbyUpdate()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "lobby") { lobby.Clear(); sentPrimary = null; return; }
            var nu = LocalUserManager.GetFirstLocalUser()?.currentNetworkUser;
            if (nu == null) return;
            string primary = Mw2Character.LocalDisplayWeaponName();
            if (primary == sentPrimary && LocalLobbyHead == sentHead && Time.unscaledTime < nextLobby) return;
            sentPrimary = primary; sentHead = LocalLobbyHead; nextLobby = Time.unscaledTime + 2f;
            var w = new NetworkWriter();
            w.Write(nu.netId.Value); w.Write(primary ?? ""); w.Write(LocalLobbyHead);
            Send(KLobby, w.ToArray());
        }

        static void ReadLobby(NetworkReader r)
        {
            uint id = r.ReadUInt32(); string primary = r.ReadString(); int head = r.ReadInt32();
            var mine = LocalUserManager.GetFirstLocalUser()?.currentNetworkUser;
            if (mine != null && mine.netId.Value == id && !Echo) return;
            if (!lobby.TryGetValue(id, out var was) || was.primary != primary || was.head != head)
                Plugin.Log.LogInfo($"[net] lobby: player {id} holds {primary}, head {head}");
            lobby[id] = (primary, head);
        }

        /// A teammate's character-select soldier: their class's primary and head, once they've said.
        public static bool LobbyOf(NetworkUser nu, out uint weapon, out int head)
        {
            weapon = 0; head = -1;
            if (nu == null || !lobby.TryGetValue(nu.netId.Value, out var l)) return false;
            weapon = string.IsNullOrEmpty(l.primary) ? 0 : Native.WeaponIndex(l.primary);
            head = l.head;
            return true;
        }

        /// Pilot (lobbyecho): our own display drawn from what the lobby message carried back.
        public static bool LobbyEcho;

        /// Pilot: the host's hitbox on the echo of our own aircraft (as a teammate's would get).
        public static HealthComponent EchoHitbox()
        {
            foreach (var kv in props)
                if ((kv.Key & EchoBit) != 0)
                    foreach (var p in kv.Value.Values)
                        if (p.hitbox != null) return p.hitbox.GetComponent<HealthComponent>();
            return null;
        }

        /// Host: a hitbox on a teammate's shootable aircraft while it says it is one.
        static void HostHitbox(uint owner, int id, PropView pv, float health, float radius)
        {
            if (!NetworkServer.active) return;
            if (health <= 0f || pv.go == null)
            {
                if (pv.hitbox != null) { Mw2VehicleHealth.Remove(pv.hitbox); pv.hitbox = null; }
                return;
            }
            if (pv.hitbox != null || (pv.aircraft != null && pv.aircraft.Dead)) return;
            var ownerBody = ServerFind(owner & ~EchoBit);
            if (ownerBody == null) return;
            pv.aircraft = new RemoteAircraft { owner = owner, id = id };
            pv.hitbox = Mw2VehicleHealth.Create(pv.aircraft, pv.go.transform.position, ownerBody, health, radius);
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[net] hitbox on {owner:x8}'s {pv.model} ({health:F0} MW2 health)");
        }
        static readonly Dictionary<uint, Dictionary<int, PropView>> props = new Dictionary<uint, Dictionary<int, PropView>>();

        static void ReadProps(NetworkReader r)
        {
            uint owner = r.ReadUInt32();
            int n = r.ReadUInt16();
            var me = bridge.LocalBody2;
            var shift = Vector3.zero;
            if (me != null && NetId(me) == owner) { if (!Echo) return; owner |= EchoBit; shift = EchoOffset(me); }
            if (!props.TryGetValue(owner, out var set)) props[owner] = set = new Dictionary<int, PropView>();
            foreach (var p in set.Values) p.seen = false;
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                string model = r.ReadString(), trail = r.ReadString();
                var pos = r.ReadVector3() + shift; var rot = r.ReadQuaternion(); float scale = r.ReadSingle();
                float health = r.ReadSingle(), radius = r.ReadSingle();
                bool turret = r.ReadBoolean();
                Quaternion yawRot = Quaternion.identity, pitchRot = Quaternion.identity;
                if (turret) { yawRot = r.ReadQuaternion(); pitchRot = r.ReadQuaternion(); }
                float spin = r.ReadSingle();
                if (!set.TryGetValue(id, out var pv) || pv.model != model)
                {
                    if (pv != null && pv.go != null) UnityEngine.Object.Destroy(pv.go);
                    if (pv != null && pv.hitbox != null) Mw2VehicleHealth.Remove(pv.hitbox);
                    Mw2Prop.Mirroring++;
                    GameObject go;
                    try { go = Mw2Prop.Build(model, bridge.LocalBody2 ?? AnyBody(), Space.Scale); }
                    finally { Mw2Prop.Mirroring--; }
                    set[id] = pv = new PropView { go = go, model = model, pos = pos, rot = rot };
                    if (go != null) go.transform.SetPositionAndRotation(pos, rot);
                }
                pv.pos = pos; pv.rot = rot; pv.seen = true;
                pv.turret = turret; pv.yaw = yawRot; pv.pitch = pitchRot;
                if (pv.go != null) foreach (var sp in pv.go.GetComponentsInChildren<Mw2Spin>()) if (sp.name.EndsWith("_spin")) sp.speed = spin;
                pv.trail = string.IsNullOrEmpty(trail) ? null : trail;
                if (pv.go != null) pv.go.transform.localScale = Vector3.one * scale; // vehicle model scale (Cobra 1.5...)
                HostHitbox(owner, id, pv, health, radius);
            }
            var gone = new List<int>();
            foreach (var kv in set) if (!kv.Value.seen) gone.Add(kv.Key);
            foreach (var id in gone) { var p = set[id]; if (p.go != null) UnityEngine.Object.Destroy(p.go); if (p.fx != 0) Mw2Fx.Stop(p.fx); if (p.hitbox != null) Mw2VehicleHealth.Remove(p.hitbox); set.Remove(id); }
        }

        /// Between 15 Hz updates: glide each mirrored prop toward its last pose; drag its trail.
        public static void LateUpdate()
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * 20f);
            foreach (var set in props.Values)
                foreach (var p in set.Values)
                {
                    if (p.go == null) continue;
                    var t = p.go.transform;
                    t.SetPositionAndRotation(Vector3.Lerp(t.position, p.pos, k), Quaternion.Slerp(t.rotation, p.rot, k));
                    if (p.turret)
                    {
                        var tu = p.go.GetComponent<Mw2Turret>();
                        if (tu != null && tu.yawT != null && tu.pitchT != null)
                        {
                            tu.yawT.localRotation = Quaternion.Slerp(tu.yawT.localRotation, p.yaw, k);
                            tu.pitchT.localRotation = Quaternion.Slerp(tu.pitchT.localRotation, p.pitch, k);
                        }
                    }
                    if (p.hitbox != null) Mw2VehicleHealth.Follow(p.hitbox, t.position);
                    if (p.trail != null)
                    {
                        Mw2Fx.NoBroadcast++;
                        try { if (p.fx == 0 || !Mw2Fx.Move(p.fx, t.position, -t.forward)) p.fx = Mw2Fx.Play(p.trail, t.position, -t.forward); }
                        finally { Mw2Fx.NoBroadcast--; }
                    }
                }
        }

        static void ClearProps(uint owner)
        {
            if (!props.TryGetValue(owner, out var set)) return;
            foreach (var p in set.Values) { if (p.go != null) UnityEngine.Object.Destroy(p.go); if (p.fx != 0) Mw2Fx.Stop(p.fx); if (p.hitbox != null) Mw2VehicleHealth.Remove(p.hitbox); }
            props.Remove(owner);
        }

        static CharacterBody AnyBody()
        {
            foreach (var cb in CharacterBody.readOnlyInstancesList) if (cb != null && cb.isPlayerControlled) return cb;
            return null;
        }

        // ------------------------------------------------------------------ fx / sounds

        static void SendFx(byte kind, string name, Vector3 at, Vector3 fwd)
        {
            if (!Online || string.IsNullOrEmpty(name)) return;
            var w = new NetworkWriter();
            w.Write(name); w.Write(at); w.Write(fwd);
            Send(kind, w.ToArray());
        }

        static void ReadFx(NetworkReader r, bool sound)
        {
            string name = r.ReadString(); var at = r.ReadVector3(); var fwd = r.ReadVector3();
            if (sound) { PlaySound(name, at, fwd.x > 0f ? fwd.x : 1f); return; }
            Mw2Fx.NoBroadcast++;
            try { Mw2Fx.Play(name, at, fwd.sqrMagnitude > 1e-6f ? fwd : Vector3.up); }
            finally { Mw2Fx.NoBroadcast--; }
        }

        static void PlaySound(string alias, Vector3 at, float vol)
        {
            Mw2Killstreaks.NoSoundBroadcast++;
            try { bridge.StreaksRef?.FxSound(alias, at, vol); }
            finally { Mw2Killstreaks.NoSoundBroadcast--; }
        }
    }
}
