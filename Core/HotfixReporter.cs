using System;
using System.Collections.Generic;
using UnityEngine;

namespace HotFix.HotUpdate
{
    public struct StageTiming
    {
        public HotfixStage Stage;
        public string Message;
        public float Seconds;
    }

    /// <summary>
    /// 上报接口。默认只打日志；接真实埋点时实现这个接口即可，编排层不需要改。
    /// 实现方必须保证不抛异常、不阻塞。
    /// </summary>
    public interface IHotfixReporter
    {
        void OnStage(HotfixStage stage, string message);
        void OnFinished(UpdateResult result, IReadOnlyList<StageTiming> stages);
    }

    public sealed class ConsoleHotfixReporter : IHotfixReporter
    {
        public void OnStage(HotfixStage stage, string message)
        {
            Debug.Log("[Hotfix][" + stage + "] " + message);
        }

        public void OnFinished(UpdateResult result, IReadOnlyList<StageTiming> stages)
        {
            if (result == null) return;

            var builder = new System.Text.StringBuilder();
            builder.Append("[Hotfix] 流程结束 success=").Append(result.Success)
                .Append(" error=").Append(result.Error)
                .Append(" applied=").Append(result.AppliedUpdate);

            if (stages != null)
            {
                for (int i = 0; i < stages.Count; i++)
                {
                    builder.Append("\n  ").Append(stages[i].Stage)
                        .Append(' ').Append(stages[i].Seconds.ToString("0.00")).Append('s');
                }
            }

            Debug.Log(builder.ToString());
        }
    }

    /// <summary>阶段计时器：产出可直接上报的耗时序列。</summary>
    public sealed class StageTimer
    {
        private readonly List<StageTiming> _timings = new List<StageTiming>(12);
        private HotfixStage _stage;
        private string _message;
        private float _enteredAt;
        private bool _running;

        public IReadOnlyList<StageTiming> Timings
        {
            get { return _timings; }
        }

        public void Enter(HotfixStage stage, string message)
        {
            Close();

            _stage = stage;
            _message = message;
            _enteredAt = Time.realtimeSinceStartup;
            _running = true;
        }

        public void Close()
        {
            if (!_running) return;

            _timings.Add(new StageTiming
            {
                Stage = _stage,
                Message = _message,
                Seconds = Time.realtimeSinceStartup - _enteredAt
            });
            _running = false;
        }
    }
}
