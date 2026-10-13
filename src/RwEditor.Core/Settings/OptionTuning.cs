namespace RwEditor.Core.Settings;

/// <summary>What the user changed about the TEXT of one shipped menu option. Null = as shipped. Both are stored in the file's own form (a literal \n between lines).</summary>
public sealed class OptionText
{
    public string? Name { get; set; }
    public string? Description { get; set; }

    public bool IsEmpty => Name == null && Description == null;
    public OptionText Clone() => new() { Name = Name, Description = Description };
}

/// <summary>
/// What the user wants changed in the locked option files (E5b): numbers by catalog id (knowledge/option-settings.json) and the text of options by tag.
/// Only what differs from the shipped mod is stored, so the default is "nothing" and equal choices serialise equally. This is the user's intent; the
/// lines that were actually rewritten are recorded separately as <see cref="OptionEdit"/>.
/// </summary>
public sealed class OptionTuning
{
    /// <summary>Catalog setting id (e.g. "income.credits") -> the value text.</summary>
    public SortedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Option tag (e.g. "richStart") -> its changed text.</summary>
    public SortedDictionary<string, OptionText> Texts { get; set; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Values.Count == 0 && Texts.Count == 0;

    public OptionTuning Clone() => new()
    {
        Values = new SortedDictionary<string, string>(Values, StringComparer.Ordinal),
        Texts = new SortedDictionary<string, OptionText>(Texts.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), StringComparer.Ordinal),
    };

    /// <summary>Sets one text part; a null / shipped value removes it (and the option's entry when nothing is left).</summary>
    public void SetName(string tag, string? name) => Edit(tag, t => t.Name = name);
    public void SetDescription(string tag, string? description) => Edit(tag, t => t.Description = description);

    void Edit(string tag, Action<OptionText> change)
    {
        var t = Texts.TryGetValue(tag, out var x) ? x : new OptionText();
        change(t);
        if (t.IsEmpty) Texts.Remove(tag); else Texts[tag] = t;
    }

    public void SetValue(string id, string? value)
    {
        if (value == null) Values.Remove(id); else Values[id] = value;
    }

    /// <summary>Back to the shipped text and numbers of the given option tags / setting ids.</summary>
    public void Revert(IEnumerable<string> tags, IEnumerable<string> settingIds)
    {
        foreach (var t in tags) Texts.Remove(t);
        foreach (var id in settingIds) Values.Remove(id);
    }
}

/// <summary>
/// One line of a LOCKED file that the editor rewrote (E5b). Recorded so that (1) the locked-file check can prove the file is "shipped + these lines only"
/// and (2) the shipped text can always be rebuilt (put back / change again). Line numbers do not shift because a line is only ever replaced.
/// </summary>
public sealed class OptionEdit
{
    public string File { get; set; } = "";
    /// <summary>1-based line number in the file.</summary>
    public int Line { get; set; }
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>The line as shipped, without its line break.</summary>
    public string OldLine { get; set; } = "";
    /// <summary>The line as written, without its line break.</summary>
    public string NewLine { get; set; } = "";

    public OptionEdit Clone() => (OptionEdit)MemberwiseClone();
}
