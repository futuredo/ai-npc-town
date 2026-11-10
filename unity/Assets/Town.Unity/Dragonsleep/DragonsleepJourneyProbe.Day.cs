using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        void DriveDay()
        {
            var s = _game.Session;
            switch (_phase)
            {
                case 1: Click("mode_Act"); _phase = 2; break;
                case 2:
                    _root.Q<TextField>("playerInput").value = "观察四周";
                    Click("sendButton"); _phase = 3; break;
                case 3:
                    Require(s.CaptureState().Spent == 2 && s.World.Phase == DayPhase.Dawn, "真实观察未消耗两点时间。");
                    _sceneVersion = s.World.Version; _before = State(); Click("openMap"); _phase = 4; break;
                case 4: Click("endPhase"); _phase = 5; break;
                case 5:
                    CheckDay(DayPhase.Noon, 1); _before = State(); Click("openMap"); _phase = 6; break;
                case 6: Click("endPhase"); _phase = 7; break;
                case 7:
                    CheckDay(DayPhase.Dusk, 2);
                    Result.dayCommits = _game.SceneTransaction.DayCommits;
                    Result.sceneCommits = _game.SceneTransaction.Commits; Result.finalHash = State();
                    Require(s.Traces.Count == 1 && s.Traces[0].Kind == AiTaskKind.Adjudicate &&
                        s.Traces[0].Provider.Simulated, "只允许一次离线观察，不应触发规划或居民调用。");
                    Checkpoint("实际观察耗时后，日间世界与 spent 在两次 SDK 比较/写入中一并推进"); Finish(null); break;
            }
        }

        void CheckDay(DayPhase phase, int count)
        {
            var s = _game.Session; var sdk = _game.SceneTransaction.LastReceipt; var host = s.RuleEvents.Last().Receipt;
            Require(s.World.Phase == phase && s.World.Day == 1 && s.CaptureState().Spent == 0 && s.TimeLeftInPhase == 4,
                "SDK 日间时钟与剩余时间未原子更新。");
            Require(_game.SceneTransaction.DayCommits == count && _game.SceneTransaction.Commits == count &&
                s.World.Version == _sceneVersion + count && s.World.RollCounter == 0, "日间提交次数、版本或巡逻次数错误。");
            Require(sdk.Code == "committed" && host.Status == "committed" && host.Applied && sdk.BeforeDigest == _before &&
                host.BeforeHash == _before && sdk.AfterDigest == State() && host.AfterHash == State(), "日间 SDK 与宿主回执不一致。");
        }
    }
}
