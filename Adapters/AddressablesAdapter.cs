using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 资源通道适配器。
    ///
    /// 职责边界：
    /// - 只做"按清单里的 catalog / label 把资源准备好"，不决定要不要更新；
    /// - 下载与校验交给 Addressables 自己（它有 CRC 与内容寻址缓存）；
    /// - 不与 Lua 通道互相引用，两边唯一的交汇点是 manifest。
    /// </summary>
    public sealed class AddressablesAdapter
    {
        private int _loadedCatalogCount;

        /// <summary>是否已经加载过远端 catalog。</summary>
        public bool HasRemoteCatalog
        {
            get { return _loadedCatalogCount > 0; }
        }

        /// <summary>加载远端 catalog；url 为空时视为未启用资源通道，直接成功。</summary>
        public IEnumerator LoadRemoteCatalog(string catalogUrl, Action<bool, string> onDone)
        {
            if (string.IsNullOrEmpty(catalogUrl))
            {
                Complete(onDone, true, null);
                yield break;
            }

            AsyncOperationHandle<IResourceLocator> handle;
            try
            {
                handle = Addressables.LoadContentCatalogAsync(catalogUrl, false);
            }
            catch (Exception e)
            {
                Complete(onDone, false, "发起 catalog 加载失败: " + e.Message);
                yield break;
            }

            while (!handle.IsDone) yield return null;

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Complete(onDone, false, Describe(handle.OperationException, "catalog 加载失败"));
                yield break;
            }

            _loadedCatalogCount++;
            Complete(onDone, true, null);
        }

        /// <summary>查询某个 label 需要下载的字节数；失败返回 -1。</summary>
        public IEnumerator GetDownloadSize(string label, Action<long, string> onDone)
        {
            if (string.IsNullOrEmpty(label))
            {
                if (onDone != null) onDone(0, null);
                yield break;
            }

            AsyncOperationHandle<long> handle;
            try
            {
                handle = Addressables.GetDownloadSizeAsync(label);
            }
            catch (Exception e)
            {
                if (onDone != null) onDone(-1, e.Message);
                yield break;
            }

            while (!handle.IsDone) yield return null;

            long size = -1;
            string error = null;
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                size = handle.Result;
            }
            else
            {
                error = Describe(handle.OperationException, "查询下载大小失败");
            }

            Addressables.Release(handle);
            if (onDone != null) onDone(size, error);
        }

        /// <summary>下载某个 label 的依赖。</summary>
        public IEnumerator DownloadDependencies(string label, Action<long, long> onProgress, Action<bool, string> onDone)
        {
            if (string.IsNullOrEmpty(label))
            {
                Complete(onDone, true, null);
                yield break;
            }

            AsyncOperationHandle handle;
            try
            {
                handle = Addressables.DownloadDependenciesAsync(label, false);
            }
            catch (Exception e)
            {
                Complete(onDone, false, "发起资源下载失败: " + e.Message);
                yield break;
            }

            while (!handle.IsDone)
            {
                DownloadStatus status = handle.GetDownloadStatus();
                if (onProgress != null) onProgress(status.DownloadedBytes, status.TotalBytes);
                yield return null;
            }

            bool success = handle.Status == AsyncOperationStatus.Succeeded;
            string error = success ? null : Describe(handle.OperationException, "资源下载失败");

            Addressables.Release(handle);
            Complete(onDone, success, error);
        }

        /// <summary>
        /// 清理某组资源在缓存中的旧内容。
        /// 只能走 Addressables 的接口，直接删 com.unity.addressables 目录会让 catalog 与文件不一致。
        /// </summary>
        public IEnumerator ClearCache(string label, Action onDone)
        {
            if (string.IsNullOrEmpty(label))
            {
                if (onDone != null) onDone();
                yield break;
            }

            AsyncOperationHandle<bool> handle;
            try
            {
                handle = Addressables.ClearDependencyCacheAsync(label, false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 清理资源缓存失败: " + e.Message);
                if (onDone != null) onDone();
                yield break;
            }

            while (!handle.IsDone) yield return null;

            Addressables.Release(handle);
            if (onDone != null) onDone();
        }

        private static string Describe(Exception exception, string fallback)
        {
            return exception != null && !string.IsNullOrEmpty(exception.Message) ? exception.Message : fallback;
        }

        private static void Complete(Action<bool, string> onDone, bool success, string error)
        {
            if (onDone != null) onDone(success, error);
        }
    }
}
