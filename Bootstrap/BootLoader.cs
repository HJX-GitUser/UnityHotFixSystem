using System;
using System.Collections;
using HotFix.HotUpdate;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 不可热更的引导层：跑完更新流程后才创建 XLuaManager 并进入主场景。
///
/// 放在唯一的启动场景里（例如 Boot.unity），该场景不要放 XLuaManager。
/// UI 是刻意极简的 IMGUI 占位实现，正式项目替换成 uGUI / UI Toolkit 即可，流程不需要改。
/// </summary>
public sealed class BootLoader : MonoBehaviour
{
    [SerializeField] private HotfixConfig config = new HotfixConfig();

    [Tooltip("更新完成后加载的场景名，需在 Build Settings 中。留空则停在引导场景。")]
    [SerializeField] private string mainSceneName = "Main";

    private UpdateProgress _progress;
    private string _errorText;
    private bool _running;

    private bool _waitingConfirm;
    private long _confirmBytes;
    private Action<bool> _confirmAnswer;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        // 引导流程自己决定 XLuaManager 的创建时机
        XLuaManager.SuppressAutoCreate = true;
    }

    private void Start()
    {
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        _running = true;
        _errorText = null;

        var orchestrator = new UpdateOrchestrator(config);
        orchestrator.ConfirmDownloadRequest = RequestConfirm;

        UpdateResult result = null;
        yield return orchestrator.Run(p => _progress = p, r => result = r);

        _running = false;

        if (result == null || !result.Success)
        {
            _errorText = result != null ? result.Message : "更新流程异常";
            Debug.LogError("[Boot] 更新失败: " + _errorText);
            yield break;
        }

        yield return EnterGame(result);
    }

    /// <summary>大包下载确认：挂起流程，等玩家在 OnGUI 里点按钮。</summary>
    private IEnumerator RequestConfirm(long bytes, Action<bool> answer)
    {
        _confirmBytes = bytes;
        _confirmAnswer = answer;
        _waitingConfirm = true;

        while (_waitingConfirm) yield return null;
    }

    private void AnswerConfirm(bool accepted)
    {
        if (!_waitingConfirm) return;

        _waitingConfirm = false;

        Action<bool> answer = _confirmAnswer;
        _confirmAnswer = null;
        if (answer != null) answer(accepted);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知大小";
        if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KB";
        return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
    }

    private IEnumerator EnterGame(UpdateResult result)    {
        if (!string.IsNullOrEmpty(result.LuaRootOverride))
        {
            XLuaManager.PendingLuaRoot = result.LuaRootOverride;
        }

        // 入口模块由这里显式执行，才能拿到成功/失败信号
        XLuaManager.PendingSuppressAutoRunMain = true;

        var managerObject = new GameObject("[XLuaManager]");
        XLuaManager manager = managerObject.AddComponent<XLuaManager>();
        yield return null;

        XLuaManager.SuppressAutoCreate = false;

        object[] mainResult = manager.RunMainModule();
        if (mainResult == null)
        {
            _errorText = "Lua 入口执行失败，请查看 Console";
            _progress = new UpdateProgress
            {
                Stage = HotfixStage.Failed,
                Message = _errorText,
                Progress01 = 1f
            };
            yield break;
        }

        // 到这里才算"真的能进游戏"，健康哨兵写在这里
        new RollbackService(new StorageService()).MarkHealthy(result.Manifest);

        if (!string.IsNullOrEmpty(mainSceneName))
        {
            if (Application.CanStreamedLevelBeLoaded(mainSceneName))
            {
                AsyncOperation operation = SceneManager.LoadSceneAsync(mainSceneName);
                while (operation != null && !operation.isDone) yield return null;
            }
            else
            {
                Debug.LogWarning("[Boot] 场景未加入 Build Settings，跳过加载: " + mainSceneName);
            }
        }

        Destroy(gameObject);
    }

    private void OnGUI()
    {
        if (!_running && string.IsNullOrEmpty(_errorText)) return;

        float width = Mathf.Min(Screen.width * 0.85f, 560f);
        var panel = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.5f - 70f, width, 150f);
        GUI.Box(panel, "热更新");

        GUILayout.BeginArea(new Rect(panel.x + 16f, panel.y + 30f, panel.width - 32f, panel.height - 46f));

        if (!string.IsNullOrEmpty(_errorText))
        {
            GUILayout.Label(_errorText);
            GUILayout.Space(8f);

            if (GUILayout.Button("重试"))
            {
                StartCoroutine(Run());
            }
        }
        else if (_waitingConfirm)
        {
            GUILayout.Label("本次需要下载 " + FormatSize(_confirmBytes) + "，建议在 Wi-Fi 下继续。");
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("继续下载")) AnswerConfirm(true);
            if (GUILayout.Button("稍后再说")) AnswerConfirm(false);
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.Label(_progress.Stage + " · " + _progress.Message);

            Rect bar = GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true));
            GUI.Box(bar, string.Empty);

            var fill = new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(_progress.Progress01), bar.height);
            GUI.Box(fill, string.Empty);

            GUILayout.Label(Mathf.RoundToInt(_progress.Progress01 * 100f) + "%");
        }

        GUILayout.EndArea();
    }
}
