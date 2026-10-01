using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

/// <summary>
/// Ensures the configured gameplay scene has its runtime entry point even if
/// the scene was opened before the GameRoot prefab was added to it.
/// </summary>
[Preserve]
public static class GameplaySceneBootstrap
{
    const string GameplayScenePath = "Assets/Scenes/SampleScene.unity";
    const string GameRootResourcePath = "Prefabs/GameRoot";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureGameplayEntryPoint()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != GameplayScenePath)
            return;

        if (Object.FindAnyObjectByType<DrivingCore>() != null)
            return;

        GameObject prefab = Resources.Load<GameObject>(GameRootResourcePath);
        if (prefab == null)
        {
            Debug.LogError("GAME STARTUP // GameRoot prefab is missing at Resources/Prefabs/GameRoot.");
            return;
        }

        GameObject root = Object.Instantiate(prefab);
        root.name = "VOYAGE // GAME ROOT";
        Debug.LogWarning("GAME STARTUP // SampleScene had no DrivingCore; restored the GameRoot runtime entry point.", root);
    }
}
