using System;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// TCP 连接失败后的自动重试策略（**纯计算，不碰任何 IO**，因此能被自测直接覆盖）。
    ///
    /// <para><b>要改"重试几次 / 退避多久"就改这一个文件</b>（刻意不做成配置项：它是行为约定，不是用户参数）。</para>
    ///
    /// <para>规则：</para>
    /// <list type="bullet">
    /// <item><description><see cref="MaxRetries"/> = 自动重试次数，**不含首次尝试** ——
    /// 首次连接失败后再自动重试这么多轮；全部失败就**停止**（不再无限重连），
    /// 状态显示「连接失败」，只能由用户手动点「开始连接」重来。</description></item>
    /// <item><description>退避：第 1 轮等 <see cref="StartBackoffMs"/>，之后翻倍，封顶 <see cref="MaxBackoffMs"/>。</description></item>
    /// </list>
    ///
    /// <para>重试期间按钮显示「取消重试」，点一下立刻停止（不留后台循环）。</para>
    /// </summary>
    public sealed class NpReconnectPolicy
    {
        /// <summary>自动重试次数上限（**不含首次尝试**）：首次 + 3 次重试 = 最多 4 次连接尝试。</summary>
        public const int MaxRetries = 3;

        /// <summary>第一轮重试前的等待(ms)</summary>
        public const int StartBackoffMs = 1000;

        /// <summary>退避封顶(ms)</summary>
        public const int MaxBackoffMs = 10000;

        /// <summary>退避下限(ms)：配置里填了离谱的小值时兜底</summary>
        public const int MinBackoffMs = 200;

        /// <summary>已经用掉的重试轮数（0 = 还在首次尝试阶段）</summary>
        public int Attempts { get; private set; }

        /// <summary>本轮重试要等的退避时间(ms)</summary>
        public int CurrentDelayMs { get; private set; }

        private int _nextDelayMs;

        public NpReconnectPolicy(int startBackoffMs = StartBackoffMs)
        {
            _nextDelayMs = Math.Clamp(startBackoffMs, MinBackoffMs, MaxBackoffMs);
        }

        /// <summary>重试预算已用尽</summary>
        public bool Exhausted => Attempts >= MaxRetries;

        /// <summary>
        /// 取下一轮重试（顺带给出该等的退避时间）；预算用尽返回 false —— 调用方据此**停止**重试。
        /// </summary>
        public bool TryBeginNext(out int delayMs)
        {
            if (Exhausted)
            {
                delayMs = 0;
                return false;
            }

            Attempts++;
            CurrentDelayMs = _nextDelayMs;
            delayMs = CurrentDelayMs;
            _nextDelayMs = Math.Min(_nextDelayMs * 2, MaxBackoffMs);
            return true;
        }

        /// <summary>回到"首次尝试"状态（每次用户点「开始连接」都重置）</summary>
        public void Reset()
        {
            Attempts = 0;
            CurrentDelayMs = 0;
            _nextDelayMs = Math.Clamp(StartBackoffMs, MinBackoffMs, MaxBackoffMs);
        }

        /// <summary>状态栏文案片段：等待中带剩余退避时间，正在拨号则只报次数</summary>
        public string Describe(bool waiting) => waiting
            ? $"第 {Attempts}/{MaxRetries} 次重试，{CurrentDelayMs / 1000.0:0.#}s 后再试"
            : $"第 {Attempts}/{MaxRetries} 次重试";
    }
}
