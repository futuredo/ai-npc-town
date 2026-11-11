using System.Linq;
using Town.Core.Dragonsleep;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepJourneyProbe
    {
        DsCraftProposal _oldCraft, _currentCraft;
        int _craftTime;

        void DriveCraft()
        {
            var s = _game.Session;
            switch (_phase)
            {
                case 1:
                    Click("openMap"); _phase = 2; break;
                case 2:
                    Click("travel_workshop"); _phase = 3; break;
                case 3:
                    Require(s.World.PlayerPlace == "workshop", "地图移动尚未进入工坊。");
                    _before = State(); _craftTime = s.TimeLeftInPhase;
                    Click("mode_Craft"); _phase = 4; break;
                case 4:
                    _root.Q<TextField>("playerInput").value = "用蓝晶石织一盏灯";
                    Click("sendButton"); _phase = 5; break;
                case 5:
                    _oldCraft = CheckCraftReady();
                    Click("declineCraft"); _phase = 6; break;
                case 6:
                    Require(!s.HasPendingCraft && State() == _before && s.TimeLeftInPhase == _craftTime,
                        "放弃图样不能消耗材料或时间。");
                    Require(s.Traces.Last().Receipt.Status == "declined", "放弃没有生成原有终态回执。");
                    _root.Q<TextField>("playerInput").value = "用蓝晶石织一盏灯";
                    Click("sendButton"); _phase = 7; break;
                case 7:
                    _currentCraft = CheckCraftReady();
                    Require(!ReferenceEquals(_oldCraft, _currentCraft), "同内容新图样不得复用旧授权。");
                    // 负例明确调用授权入口；正常准备、放弃和最终确认仍由实际按钮驱动。
                    Require(s.ConfirmCraft(true) == null && s.ConfirmCraft(_oldCraft, true) == null &&
                        s.ConfirmCraft(_oldCraft, false) == null, "严格会话被裸 bool 或旧句柄决定。");
                    Require(s.HasPendingCraft && State() == _before && _game.Hud.ModalOpen,
                        "旧句柄不得清除新候选或改变世界。");
                    Result.oldCraftRejected = true;
                    Click("confirmCraft"); _phase = 8; break;
                case 8:
                    Require(!s.HasPendingCraft && !_game.Hud.ModalOpen && s.TimeLeftInPhase == _craftTime - 2,
                        "绑定确认没有恰好一次扣除时间并收起图样。");
                    var craft = s.Traces.Last();
                    Require(craft.CraftDecision == "confirmed" && craft.Receipt.Applied && craft.Receipt.Status == "committed" &&
                        s.World.Items["crystal_blue"].Holder == "consumed" && s.World.Items.Values.Count(i => i.Crafted) == 1,
                        "材料或实际造物回执不一致。");
                    Result.finalHash = State();
                    Require(s.ConfirmCraft(_currentCraft, true) == null && State() == Result.finalHash,
                        "重复句柄不得重复提交。");
                    Require(s.Traces.Count == 2 && s.Traces.All(t => t.Kind == AiTaskKind.Craft && t.Provider.Simulated),
                        "确认不能产生第二次提供方调用。");
                    Result.boundCraftConfirmed = true;
                    Checkpoint("图样准备与放弃不写世界；旧授权拒绝，当前授权恰好提交一次且确认不调用模型");
                    Finish(null); break;
            }
        }

        DsCraftProposal CheckCraftReady()
        {
            var s = _game.Session;
            Require(s.HasPendingCraft && State() == _before && s.TimeLeftInPhase == _craftTime,
                "图样准备改变了世界或时间。");
            var trace = s.Traces.Last();
            var handle = s.GetCraftProposal(trace);
            Require(handle != null && trace.CraftDecision == "pending" && trace.Receipt.Status == "awaiting_confirmation",
                "图样与实际请求没有确切绑定。");
            Require(_root.Q("craftProposal") != null && _root.Query<Label>().ToList().Any(l => l.text == handle.Description),
                "界面没有显示绑定图样的冻结描述。");
            return handle;
        }
    }
}
