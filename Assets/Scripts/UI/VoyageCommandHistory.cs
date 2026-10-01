using System.Collections.Generic;

/// <summary>Session history, with a separate unfinished input while browsing.</summary>
public sealed class VoyageCommandHistory
{
    const int Capacity = 100;
    readonly List<string> entries = new List<string>(Capacity);
    int cursor;
    string draft = "/";

    public void ResetNavigation() { cursor = entries.Count; draft = "/"; }

    public void Record(string input)
    {
        string text = (input ?? "").Trim();
        if (text.Length > 0 && text != "/" && (entries.Count == 0 || entries[entries.Count - 1] != text))
        {
            if (entries.Count == Capacity) entries.RemoveAt(0);
            entries.Add(text);
        }
        ResetNavigation();
    }

    public string Previous(string current)
    {
        if (entries.Count == 0) return current;
        if (cursor == entries.Count) draft = current;
        if (cursor > 0) cursor--;
        return entries[cursor];
    }

    public string Next(string current)
    {
        if (cursor >= entries.Count) return current;
        cursor++;
        return cursor == entries.Count ? draft : entries[cursor];
    }
}
