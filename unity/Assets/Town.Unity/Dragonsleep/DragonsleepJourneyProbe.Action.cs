using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        int _actionExpected;
        string _actionBefore;

        void DriveAction()
        {
            if (_phase == 1)
            {
                Click("mode_Act");
                _phase = 2;
                return;
            }
            if (_phase == 2)
            {
                if (_actionExpected == 12)
                {
                    var kernel = _game.SceneTransaction.ActionKernel;
                    Result.actionCommits = kernel.Commits;
                    Result.actionRetainedRequests = kernel.RetainedRequestCount;
                    Result.actionRetainedBeforeUtf8Bytes = kernel.RetainedBeforeUtf8Bytes;
                    Require(Result.actionCommits == 12 && Result.actionRetainedRequests == 12, "每次行动须恰好提交一次。");
                    Require(Result.actionRetainedBeforeUtf8Bytes > 0, "未记录 before JSON 的累计 UTF-8 字节量。");
                    Require(_game.ActionTransaction.PrivateRecordList.Count == 12, "每次 ready 须有私有录制。");
                    var last = _game.Session.Traces.Last(trace => trace.KernelCode != null).Prompt.TaskSnapshot;
                    Require(last.ActionPerceptionVersion == DsActionPerception.Current &&
                        last.ToDictionary().TryGetValue("recent_player_actions", out var history) &&
                        ((System.Collections.Generic.List<object>)history).Count == 4,
                        "后续真实行动须收到最多四条冻结亲历记录，而不是整个 trace 历史。");
                    Checkpoint("十二次自由行动由内核唯一提交，并测量当前局保留量");
                    Finish(null);
                    return;
                }
                _actionBefore = _game.Session.CaptureState().Hash;
                _root.Q<TextField>("playerInput").value = "观察四周";
                Click("sendButton");
                _actionExpected++;
                _phase = 3;
                return;
            }
            var trace = _game.Session.Traces.Last(t => t.KernelCode != null);
            var receipt = _game.SceneTransaction.ActionKernel.LastReceipt;
            Require(trace.Kind == AiTaskKind.Adjudicate && trace.KernelCode == "committed" && trace.Receipt.Applied,
                "真实自由行动没有成功提交。");
            Require(receipt.BeforeDigest == _actionBefore && receipt.AfterDigest == trace.Receipt.AfterHash,
                "内核与宿主回执摘要不一致。");
            Require(_game.ActionCommits == _actionExpected && _game.ActionKernelDigest == _game.Session.CaptureState().Hash,
                "行动后权威状态或提交计数不一致。");
            // 结束时段的回顾是独立 UI；收起后才发送下一次自由行动，不能绕过读取阻断。
            if (_game.Hud.ModalOpen) Click("closeRecap");
            _phase = 2;
        }
    }
}
