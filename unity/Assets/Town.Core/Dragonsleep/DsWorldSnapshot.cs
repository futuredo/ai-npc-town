using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Town.Core.Dragonsleep
{
    /// <summary>仅供本机事务和回放。包含世界真值，绝不能进入模型提示词。</summary>
    public sealed class DsWorldSnapshot
    {
        public const string Format = "dragonsleep-world-v1";
        public string Json { get; }
        public string Hash { get; }
        public int Spent { get; }
        public int PhaseBudget { get; }

        DsWorldSnapshot(string json, int spent, int budget)
        { Json = json; Hash = Digest(json); Spent = spent; PhaseBudget = budget; }

        public static DsWorldSnapshot Capture(DsWorld w, int spent, int budget)
        {
            var data = new Dictionary<string, object>
            {
                ["format"] = Format, ["spent"] = spent, ["phase_budget"] = budget,
                ["day"] = w.Day, ["phase"] = (int)w.Phase, ["version"] = w.Version,
                ["session"] = w.SessionGen, ["seed"] = w.Seed, ["roll_counter"] = w.RollCounter,
                ["player_place"] = w.PlayerPlace, ["tension"] = w.Tension, ["coins"] = w.Coins,
                ["major_events"] = w.MajorEventsToday,
                ["actors"] = w.Actors.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(a => (object)new Dictionary<string, object>
                {
                    ["id"] = a.Id, ["name"] = a.Name, ["role"] = a.Role, ["persona"] = a.Persona,
                    ["goal"] = a.Goal, ["values"] = a.Values, ["place"] = a.Place,
                    ["trust"] = a.Trust, ["fear"] = a.Fear, ["persuadable"] = a.Persuadable
                }).ToList(),
                ["beliefs"] = w.Beliefs.Select(b => (object)new Dictionary<string, object>
                {
                    ["id"] = b.Id, ["owner"] = b.Owner, ["text"] = b.Text, ["topic"] = b.Topic,
                    ["reveal_trust"] = b.RevealTrust, ["true"] = b.True, ["source"] = b.Source
                }).ToList(),
                ["player_knows"] = w.PlayerKnows.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                ["laws"] = w.Laws.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(l => (object)new Dictionary<string, object>
                { ["id"] = l.Id, ["text"] = l.Text, ["support"] = l.SupportTags, ["hinder"] = l.HinderTags, ["common"] = l.Common }).ToList(),
                ["items"] = w.Items.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(i => (object)new Dictionary<string, object>
                { ["id"] = i.Id, ["name"] = i.Name, ["holder"] = i.Holder, ["tags"] = i.Tags.OrderBy(x => x, StringComparer.Ordinal).ToArray(), ["look"] = i.Look, ["crafted"] = i.Crafted }).ToList(),
                ["places"] = Map(w.Places), ["flags"] = Map(w.Flags), ["chronicle"] = w.Chronicle.ToArray(), ["public_news"] = w.PublicNews.ToArray(),
                ["relations"] = w.Relations.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Value),
                ["whelp"] = new Dictionary<string, object>
                {
                    ["place"] = w.Whelp.Place, ["hidden"] = w.Whelp.Hidden, ["hunger"] = w.Whelp.Hunger,
                    ["fear"] = w.Whelp.Fear, ["trust"] = w.Whelp.TrustPlayer,
                    ["captured_by"] = w.Whelp.CapturedBy, ["with_mother"] = w.Whelp.WithMother,
                    ["following"] = w.Whelp.Following
                }
            };
            return new DsWorldSnapshot(MiniJson.Write(data), spent, budget);
        }

        static Dictionary<string, object> Map(Dictionary<string, string> map) =>
            map.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Value);

        public static DsWorldSnapshot Parse(string json)
        {
            var d = (Dictionary<string, object>)MiniJson.Parse(json);
            if (d.Str("format") != Format) throw new FormatException("unsupported_world_snapshot");
            int spent = (int)d.Num("spent").Value, budget = (int)d.Num("phase_budget").Value;
            if (budget <= 0 || spent < 0 || spent >= budget) throw new FormatException("invalid_time_budget");
            // 规范化，防止 JSON 空白或属性顺序造成假的校验差异。
            var snapshot = new DsWorldSnapshot(json, spent, budget);
            return Capture(snapshot.Restore(), spent, budget);
        }

        public DsWorld Restore()
        {
            var d = (Dictionary<string, object>)MiniJson.Parse(Json);
            var w = new DsWorld
            {
                Day = Int(d, "day"), Phase = (DayPhase)Int(d, "phase"), Version = Int(d, "version"),
                SessionGen = Int(d, "session"), Seed = Int(d, "seed"), RollCounter = Int(d, "roll_counter"),
                PlayerPlace = d.Str("player_place"), Tension = Float(d, "tension"), Coins = Int(d, "coins"), MajorEventsToday = Int(d, "major_events")
            };
            foreach (var a in Objects(d, "actors"))
                w.Actors.Add(a.Str("id"), new DsActor
                {
                    Id = a.Str("id"), Name = a.Str("name"), Role = a.Str("role"), Persona = a.Str("persona"),
                    Goal = a.Str("goal"), Values = a.StrList("values").ToArray(), Place = a.Str("place"),
                    Trust = Float(a, "trust"), Fear = Float(a, "fear"), Persuadable = a.StrList("persuadable").ToArray()
                });
            foreach (var b in Objects(d, "beliefs"))
                w.Beliefs.Add(new DsBelief { Id = b.Str("id"), Owner = b.Str("owner"), Text = b.Str("text"), Topic = b.Str("topic"),
                    RevealTrust = Float(b, "reveal_trust"), True = Bool(b, "true"), Source = b.Str("source") });
            w.PlayerKnows.UnionWith(d.StrList("player_knows"));
            foreach (var l in Objects(d, "laws"))
                w.Laws.Add(l.Str("id"), new DsLaw { Id = l.Str("id"), Text = l.Str("text"), SupportTags = l.StrList("support").ToArray(),
                    HinderTags = l.StrList("hinder").ToArray(), Common = Bool(l, "common") });
            foreach (var i in Objects(d, "items"))
                w.Items.Add(i.Str("id"), new DsItem { Id = i.Str("id"), Name = i.Str("name"), Holder = i.Str("holder"),
                    Tags = new HashSet<string>(i.StrList("tags")), Look = i.Str("look"), Crafted = Bool(i, "crafted") });
            foreach (var kv in d.Obj("places")) w.Places.Add(kv.Key, (string)kv.Value);
            foreach (var kv in d.Obj("flags")) w.Flags.Add(kv.Key, (string)kv.Value);
            if (d.Obj("relations") != null) foreach (var kv in d.Obj("relations")) w.Relations.Add(kv.Key, (float)(double)kv.Value);
            w.Chronicle.AddRange(d.StrList("chronicle"));
            w.PublicNews.AddRange(d.StrList("public_news"));
            var h = d.Obj("whelp");
            w.Whelp = new DsWhelp { Place = h.Str("place"), Hidden = Bool(h, "hidden"), Hunger = Float(h, "hunger"),
                Fear = Float(h, "fear"), TrustPlayer = Float(h, "trust"), CapturedBy = h.Str("captured_by"), WithMother = Bool(h, "with_mother"),
                Following = h.ContainsKey("following") && Bool(h, "following") };
            return w;
        }

        internal static void CopyInto(DsWorld from, DsWorld to)
        {
            // 保留 World 本体身份，既有场景引用继续有效；候选对象在提交后不再暴露给外部。
            to.Day = from.Day; to.Phase = from.Phase; to.Version = from.Version; to.SessionGen = from.SessionGen;
            to.Seed = from.Seed; to.RollCounter = from.RollCounter; to.PlayerPlace = from.PlayerPlace;
            to.Tension = from.Tension; to.Coins = from.Coins; to.MajorEventsToday = from.MajorEventsToday;
            Replace(from.Actors, to.Actors); Replace(from.Laws, to.Laws); Replace(from.Items, to.Items);
            Replace(from.Places, to.Places); Replace(from.Flags, to.Flags);
            Replace(from.Relations, to.Relations);
            to.Beliefs.Clear(); to.Beliefs.AddRange(from.Beliefs);
            to.PlayerKnows.Clear(); to.PlayerKnows.UnionWith(from.PlayerKnows);
            to.Chronicle.Clear(); to.Chronicle.AddRange(from.Chronicle); to.Whelp = from.Whelp;
            to.PublicNews.Clear(); to.PublicNews.AddRange(from.PublicNews);
        }

        static void Replace<T>(Dictionary<string, T> from, Dictionary<string, T> to)
        { to.Clear(); foreach (var kv in from) to.Add(kv.Key, kv.Value); }
        static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> d, string key) => ((List<object>)d[key]).Cast<Dictionary<string, object>>();
        static int Int(Dictionary<string, object> d, string key) => (int)d.Num(key).Value;
        static float Float(Dictionary<string, object> d, string key) => (float)d.Num(key).Value;
        static bool Bool(Dictionary<string, object> d, string key) => (bool)d[key];

        public static string Digest(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        public static IReadOnlyList<WorldChange> Diff(DsWorldSnapshot before, DsWorldSnapshot after)
        {
            var a = new Dictionary<string, string>(); var b = new Dictionary<string, string>();
            Flatten("", MiniJson.Parse(before.Json), a); Flatten("", MiniJson.Parse(after.Json), b);
            return a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal)
                .Where(k => !a.TryGetValue(k, out var av) || !b.TryGetValue(k, out var bv) || av != bv)
                .Select(k => new WorldChange { Path = k, Before = a.TryGetValue(k, out var av) ? av : null, After = b.TryGetValue(k, out var bv) ? bv : null }).ToArray();
        }

        static void Flatten(string path, object value, Dictionary<string, string> result)
        {
            if (value is Dictionary<string, object> map)
                foreach (var kv in map) Flatten(path.Length == 0 ? kv.Key : path + "." + kv.Key, kv.Value, result);
            else if (value is List<object> list && list.All(x => x is Dictionary<string, object> d && d.Str("id") != null))
                foreach (Dictionary<string, object> d in list) Flatten(path + "." + d.Str("id"), d, result);
            else result[path] = MiniJson.Write(value);
        }
    }
}
