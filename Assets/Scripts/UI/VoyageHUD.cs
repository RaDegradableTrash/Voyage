using UnityEngine;

public sealed class VoyageHUD : MonoBehaviour
{
    GUIStyle style;

    void OnGUI()
    {
        DrivingCore core = DrivingCore.Instance;
        if (core == null || core.Player == null) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.white;
        }
        GUI.Label(new Rect(24f, 20f, 280f, 32f), Mathf.RoundToInt(core.HudSpeedKmh) + " KM/H", style);
        GUI.Label(new Rect(24f, Screen.height - 34f, Screen.width - 48f, 24f), "WASD DRIVE   SHIFT BOOST   H LIGHTS   R RESET   P / ESC PAUSE", style);
        if (!core.HudPaused) return;
        GUI.Box(new Rect(Screen.width * 0.5f - 150f, Screen.height * 0.5f - 48f, 300f, 96f), GUIContent.none);
        GUI.Label(new Rect(Screen.width * 0.5f - 100f, Screen.height * 0.5f - 18f, 200f, 28f), "PAUSED", style);
        GUI.Label(new Rect(Screen.width * 0.5f - 115f, Screen.height * 0.5f + 14f, 230f, 24f), "P / ESC TO RESUME", style);
    }
}
