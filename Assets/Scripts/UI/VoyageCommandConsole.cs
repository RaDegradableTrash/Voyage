using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using Voyage.Lighting;

/// <summary>Small in-game command prompt. Times use the lighting clock's 0–24 hours.</summary>
[DefaultExecutionOrder(-1000)]
public sealed class VoyageCommandConsole : MonoBehaviour
{
    public static bool IsOpen { get; private set; }
    public static bool ConsumedInputThisFrame => consumedFrame == Time.frameCount;
    static int consumedFrame = -1;
    string command = "/";
    string feedback = "";
    float feedbackUntil;
    bool focusInput;
    int openedFrame;

    void Update()
    {
        bool slash = Keyboard.current != null && Keyboard.current.slashKey.wasPressedThisFrame;
        if (!slash) slash = Input.GetKeyDown(KeyCode.Slash);
        if (!IsOpen && slash)
        {
            IsOpen = true;
            command = "/";
            focusInput = true;
            openedFrame = Time.frameCount;
            consumedFrame = Time.frameCount;
        }
    }

    void OnDisable() => Close();

    void Close()
    {
        IsOpen = false;
        consumedFrame = Time.frameCount;
    }

    void OnGUI()
    {
        if (!IsOpen)
        {
            if (Time.unscaledTime < feedbackUntil)
                GUI.Box(new Rect(20, Screen.height - 75, Mathf.Min(700, Screen.width - 40), 45), feedback);
            return;
        }

        Event evt = Event.current;
        if (evt.type == EventType.KeyDown)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                Close();
                evt.Use();
                return;
            }
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                bool success = Execute(command, out feedback);
                feedbackUntil = Time.unscaledTime + 5f;
                if (success) Close();
                else focusInput = true;
                evt.Use();
                return;
            }
            if (Time.frameCount == openedFrame && (evt.keyCode == KeyCode.Slash || evt.character == '/'))
                evt.Use();
        }

        float width = Mathf.Min(700, Screen.width - 40);
        GUI.Box(new Rect(20, Screen.height - 125, width, 105), "Commands: /time 18   /fuel 25   /help");
        GUI.SetNextControlName("VoyageCommandInput");
        command = GUI.TextField(new Rect(30, Screen.height - 90, width - 20, 28), command);
        GUI.Label(new Rect(30, Screen.height - 57, width - 20, 25), feedback);
        if (focusInput)
        {
            GUI.FocusControl("VoyageCommandInput");
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.cursorIndex = editor.selectIndex = command.Length;
            if (evt.type == EventType.Repaint) focusInput = false;
        }
    }

    public static bool Execute(string input, out string result)
    {
        string[] parts = (input ?? "").Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            result = "/time <0–24 hours> | /fuel <amount> | Esc: close";
            return true;
        }
        if (parts.Length != 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || float.IsNaN(value) || float.IsInfinity(value))
        {
            result = "Usage: /time <0–24> or /fuel <positive amount>";
            return false;
        }
        if (parts[0].Equals("/time", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0f || value > 24f) { result = "Time must be between 0 and 24 hours."; return false; }
            if (DayNightSystem.Instance == null) { result = "Day/night system is unavailable."; return false; }
            DayNightSystem.Instance.SetTime(value);
            result = $"Time: {DayNightSystem.Instance.currentTime:0.##} h";
            return true;
        }
        if (parts[0].Equals("/fuel", StringComparison.OrdinalIgnoreCase))
        {
            if (value <= 0f) { result = "Fuel amount must be positive."; return false; }
            var player = DrivingCore.Instance != null ? DrivingCore.Instance.Player : null;
            var car = player != null ? player.GetComponent<CarControl>() : null;
            if (car == null) { result = "Wait for the vehicle to finish loading."; return false; }
            float before = FuelTank.SharedFuel;
            car.AddFuel(value);
            result = $"Added {FuelTank.SharedFuel - before:0.##}; fuel {FuelTank.SharedFuel:0.##}/{FuelTank.SharedCapacity:0.##}";
            return true;
        }
        result = "Unknown command. Use /help.";
        return false;
    }
}
