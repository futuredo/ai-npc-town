using System.Linq;
using Town.Core.Dragonsleep;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        int _sceneVersion;
        int _sceneTimeLeft;
        string _sceneClock;

        void DriveScene()
        {
            switch (_phase)
            {
                case 1:
                    Require(_game.Session.World.PlayerPlace == "market" && _game.SceneTransaction.Commits == 0, "新局移动入口状态不符。");
                    _sceneVersion = _game.Session.World.Version;
                    _sceneTimeLeft = _game.Session.TimeLeftInPhase;
                    _sceneClock = _game.Session.World.ClockText;
                    _before = State(); Click("openMap"); _phase = 2; break;
                case 2: Click("travel_workshop"); _phase = 3; break;
                case 3:
                    CheckSceneMove("workshop", 1);
                    _before = State(); Click("openMap"); _phase = 4; break;
                case 4: Click("travel_market"); _phase = 5; break;
                case 5:
                    CheckSceneMove("market", 2);
                    Result.sceneCommits = _game.SceneTransaction.Commits;
                    Result.finalHash = State();
                    Require(_game.Session.Traces.Count == 0, "无模型的地图移动不应触发提供方。");
                    Checkpoint("实际地图两次移动均由 SDK 比较/写入后生成宿主回执，未消耗时间或调用模型");
                    Finish(null); break;
            }
        }

        void CheckSceneMove(string place, int count)
        {
            var session = _game.Session;
            var sdk = _game.SceneTransaction.LastReceipt;
            var host = session.RuleEvents.Last().Receipt;
            Require(session.World.PlayerPlace == place && _game.SceneTransaction.Commits == count, "地图移动未进入真实 SDK 入口。");
            Require(session.World.Version == _sceneVersion + count && session.TimeLeftInPhase == _sceneTimeLeft &&
                session.World.ClockText == _sceneClock, "移动版本或零耗时语义改变。");
            Require(sdk != null && sdk.Code == "committed" && host.Status == "committed" && host.Applied &&
                sdk.BeforeDigest == _before && host.BeforeHash == _before && sdk.AfterDigest == State() && host.AfterHash == State(),
                "SDK 与宿主回执摘要不一致。");
            Require(session.World.Whelp.Hidden && !session.World.Whelp.Following, "地图移动不应凭空带上雏龙。");
        }
    }
}
