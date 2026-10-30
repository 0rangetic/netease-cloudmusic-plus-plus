using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NeteaseToolbox
{
    /// <summary>多人一起听补丁的一条运行消息（原生 WPF 页面用）。</summary>
    internal sealed class MultiListenMessageEntry
    {
        internal MultiListenMessageEntry(string time, string text, bool isError)
        {
            Time = time ?? "";
            Text = text ?? "";
            IsError = isError;
        }

        public string Time { get; private set; }
        public string Text { get; private set; }
        public bool IsError { get; private set; }
    }

    /// <summary>
    /// 多人一起听页面的只读快照。页面逻辑仍在 MultiListen.cs；这里只是把已有状态
    /// 整理给 MultiListenView，避免 WPF 页面重复探测（探测需要启动 Node 进程）。
    /// </summary>
    internal sealed class MultiListenSnapshot
    {
        public string Status = "";
        public string Detail = "";
        public string Error = "";
        public bool Busy;
        public bool Installed;
        public List<MultiListenMessageEntry> Messages = new List<MultiListenMessageEntry>();
    }

    internal sealed partial class ToolboxForm
    {
        // 原生 WPF 页面（MultiListenView）是否正显示；决定后台状态探测是否需要继续。
        private bool multiListenWpfActive;

        /// <summary>读取页面状态缓存。只做内存拷贝，不启动任何外部进程，可在 1–2 秒轮询中调用。</summary>
        internal MultiListenSnapshot ReadMultiListenSnapshot()
        {
            var snapshot = new MultiListenSnapshot {
                Status = multiListenStatusCache,
                Detail = multiListenDetailCache,
                Error = multiListenErrorCache,
                Busy = multiListenBusy,
                Installed = multiListenInstalled
            };
            snapshot.Messages.AddRange(multiListenMessageCache);
            return snapshot;
        }

        /// <summary>导航到“多人一起听”页时调用：启动后台轮询并立即探测一次，不阻塞界面。</summary>
        internal void EnsureMultiListenOpened()
        {
            multiListenWpfActive = true;
            if (IsDisposed) return;
            if (!multiListenTimer.Enabled) multiListenTimer.Start();
            if (!multiListenBusy) _ = ProbeMultiListenAsync();
        }

        /// <summary>离开“多人一起听”页时调用：停止为 WPF 页面做的后台探测。</summary>
        internal void LeaveMultiListenView()
        {
            multiListenWpfActive = false;
        }

        internal Task StartMultiListenAsync() { return StartMultiListen(); }

        internal Task RefreshMultiListenStatusAsync() { return RefreshMultiListenStatus(true); }

        internal Task StopMultiListenAsync() { return StopMultiListenConfirmed(); }

        internal void OpenMultiListenLogFolderAction() { OpenMultiListenLogFolder(); }

        private async Task ProbeMultiListenAsync()
        {
            try {
                if (IsDisposed || multiListenBusy) return;
                await RefreshMultiListenStatus(false);
            } catch (Exception ex) {
                if (!IsDisposed) ReportTask("多人一起听", "失败", "状态刷新异常：" + ex.Message);
            }
        }
    }
}
