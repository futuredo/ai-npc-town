using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Town.Core.Dragonsleep;
using UnityEngine;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class DragonsleepGame
    {
        public DsPlanRun ActivePlan { get; private set; }
        public string PlanOperation { get; private set; }
        public string PlanNotice { get; private set; } = "";
        public bool PlanBusy => PlanOperation != null;
        public bool CanCancelPlan => PlanOperation == "propose" || PlanOperation == "replan";
        public bool PlanCancellationRequested => _planCancellation?.IsCancellationRequested == true;
        public int PlanResultOffset { get; private set; }
        CancellationTokenSource _planCancellation;
        int _planOperationId;

        public Task ProposePlanAsync(string goal, DsPlanConstraints constraints = null)
        {
            if (Busy || Ending != null || Session.HasPendingCraft || string.IsNullOrWhiteSpace(goal)) return Task.CompletedTask;
            ActivePlan?.Abandon();
            ActivePlan = new DsPlanRun(Session, goal, requireHomecoming: true, constraints: constraints);
            PlanResultOffset = 0;
            return RunPlanOperationAsync("propose", async ct => { await ActivePlan.ProposeAsync(ct); });
        }

        public void AcceptPlan(IDictionary<string, string> selection)
        {
            if (Busy || Ending != null || ActivePlan?.Status != "awaiting_confirmation") return;
            try
            {
                ActivePlan.Accept(selection);
                PlanNotice = ActivePlan.Status == "running" ? "取舍已确认。每次只执行一步，不会自动执行后续步骤。" : ActivePlan.LastReason;
            }
            catch (InvalidOperationException e) { PlanNotice = e.Message; }
            catch (ArgumentException e) { PlanNotice = e.Message; }
            Hud?.RefreshPlan();
        }

        public Task ExecutePlanStepAsync()
        {
            if (Busy || Ending != null || ActivePlan?.Status != "running") return Task.CompletedTask;
            return RunPlanOperationAsync("step", async ct => { await ActivePlan.StepAsync(ct); });
        }

        public Task ReplanAsync()
        {
            if (Busy || Ending != null || ActivePlan == null) return Task.CompletedTask;
            if (ActivePlan.LatestPlanTrace?.Provider?.Error != null) return Task.CompletedTask;
            if (ActivePlan.Status == "needs_replan")
            {
                PlanResultOffset = ActivePlan.Results.Count;
                return RunPlanOperationAsync("replan", async ct => { await ActivePlan.ReplanAsync(ct); });
            }
            if (ActivePlan.Status == "planning" && ActivePlan.LatestPlanTrace?.Provider?.Error == null && ActivePlan.LatestPlanTrace != null)
            {
                PlanResultOffset = ActivePlan.Results.Count;
                return RunPlanOperationAsync("replan", async ct => { await ActivePlan.RepairProposalAsync(ct); });
            }
            return Task.CompletedTask;
        }

        public void CancelPlanRequest()
        {
            if (!CanCancelPlan || _planCancellation == null) return;
            _planCancellation.Cancel();
            PlanNotice = "已请求取消，等待提供方结束；迟到回复不会用于确认或执行。";
            Hud?.RefreshPlanControls();
        }

        public void AbandonPlan()
        {
            if (Busy || ActivePlan == null) return;
            ActivePlan.Abandon();
            PlanNotice = "计划已放弃；已经提交的步骤不会撤销。可以重新提出目标。";
            Hud?.RefreshPlan();
        }

        async Task RunPlanOperationAsync(string operation, Func<CancellationToken, Task> action)
        {
            Busy = true; PlanOperation = operation; PlanNotice = "";
            var run = ActivePlan;
            int operationId = ++_planOperationId, generation = Session.World.SessionGen;
            int firstTrace = Session.Traces.Count;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _planCancellation = cancellation;
            Hud?.RefreshPlan();
            try
            {
                await action(cancellation.Token);
                if (this == null || operationId != _planOperationId || ActivePlan != run || Session.World.SessionGen != generation)
                { run.Abandon(); return; }
                if (cancellation.IsCancellationRequested && operation != "step")
                { run.Abandon(); PlanNotice = "规划已取消，提案未执行；目标文字已保留。"; }
                else if (operation == "step")
                    PlanNotice = run.Status == "needs_replan" ? run.LastReason : "本步已结束，请查看实际回执；后续步骤尚未执行。";
                else if (operation != "step" && run.LatestPlanTrace != null)
                {
                    Hud?.PresentPlanTrace(run.LatestPlanTrace);
                    var plan = run.LatestPlanTrace.PlanReport?.Plan;
                    PlanNotice = run.Constraints.Check(plan, plan?.DefaultSelection()) ?? run.LatestPlanTrace.Presented;
                }
            }
            catch (OperationCanceledException)
            {
                run.Abandon();
                PlanNotice = operation == "step" ? "执行已中断，剩余计划已放弃；已提交的后果不会撤销，请重新提出目标。" : "规划已取消，提案未执行。";
            }
            catch (Exception e)
            {
                run.Abandon();
                if (this != null) Debug.LogWarning("计划操作中断：" + e.GetType().Name);
                PlanNotice = "操作未完成（" + e.GetType().Name + "），剩余计划已放弃；已提交的后果保留，不会重复执行，请重新提出目标。";
            }
            finally
            {
                cancellation.Dispose();
                if (operationId == _planOperationId)
                {
                    if (this != null && Session.World.SessionGen == generation && operation == "step")
                    {
                        // 异常前也可能已移动、推进时间或提交居民行动；先同步实际世界，再解忙。
                        var place = Session.World.PlayerPlace;
                        if (!_places.ContainsKey(place)) place = place == "castle" ? "castle_gate" : place == "skull_hill" ? "dragon_skull" : place;
                        SyncPlayerPlace(place); ReflectWorld();
                        foreach (var trace in Session.Traces.Skip(firstTrace)) Hud?.PresentPlanTrace(trace);
                        if (Session.World.FinalDawn) { Ending = DsEndings.Evaluate(Session.World); Hud?.ShowEnding(Ending); }
                    }
                    _planCancellation = null; PlanOperation = null; Busy = false;
                    if (this != null) { Hud?.FinishPlanOperation(); Hud?.RefreshPlan(); }
                }
            }
        }
    }
}
