using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CommDetect.Core;
/// <summary>
/// Lightweight INI file parser and writer.
/// Supports sections, key=value pairs, comments (# and ;), and inline comments.
/// 
/// Format:
///   # This is a comment
///   [SectionName]
///   key=value
///   key=value  ; inline comment
/// 
/// Section names and keys are case-insensitive.
/// Values are trimmed of whitespace. Quotes around values are stripped.
/// </summary>
public class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sectionComments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _inlineComments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _headerComments = new();
    private string _filePath = string.Empty;

    /// <summary>All section names in order of appearance.</summary>
    private readonly List<string> _sectionOrder = new();

    /// <summary>Key order within each section.</summary>
    private readonly Dictionary<string, List<string>> _keyOrder = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Sections => _sectionOrder.AsReadOnly();

    // ── Loading ─────────────────────────────────────────────────────────

    /// <summary>Load an INI file from disk.</summary>
    public static IniFile Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"INI file not found: {filePath}");

        var ini = new IniFile { _filePath = filePath };
        ini.Parse(File.ReadAllLines(filePath));
        return ini;
    }

    /// <summary>Load from string content (useful for testing or embedded defaults).</summary>
    public static IniFile LoadFromString(string content)
    {
        var ini = new IniFile();
        ini.Parse(content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray());
        return ini;
    }

    /// <summary>Try to load a file; returns a default empty IniFile if not found.</summary>
    public static IniFile LoadOrDefault(string filePath)
    {
        if (File.Exists(filePath))
            return Load(filePath);

        return new IniFile { _filePath = filePath };
    }

    // ── Reading Values ──────────────────────────────────────────────────

    public string? GetString(string section, string key, string? defaultValue = null)
    {
        if (_sections.TryGetValue(section, out var dict) &&
            dict.TryGetValue(key, out var value))
            return value;
        return defaultValue;
    }

    public int GetInt(string section, string key, int defaultValue = 0)
    {
        var str = GetString(section, key);
        return str != null && int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val)
            ? val : defaultValue;
    }

    public double GetDouble(string section, string key, double defaultValue = 0.0)
    {
        var str = GetString(section, key);
        return str != null && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
            ? val : defaultValue;
    }

    public bool GetBool(string section, string key, bool defaultValue = false)
    {
        var str = GetString(section, key);
        if (str == null) return defaultValue;

        return str.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => defaultValue
        };
    }

    public string[] GetStringArray(string section, string key, char separator = ',')
    {
        var str = GetString(section, key);
        if (string.IsNullOrWhiteSpace(str)) return Array.Empty<string>();
        return str.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Get all keys in a section.</summary>
    public IReadOnlyDictionary<string, string>? GetSection(string section)
    {
        return _sections.TryGetValue(section, out var dict) ? dict : null;
    }

    public bool HasSection(string section) => _sections.ContainsKey(section);
    public bool HasKey(string section, string key) =>
        _sections.TryGetValue(section, out var dict) && dict.ContainsKey(key);

    // ── Writing Values ──────────────────────────────────────────────────

    public void SetString(string section, string key, string value, string? comment = null)
    {
        EnsureSection(section);
        _sections[section][key] = value;

        if (!_keyOrder[section].Contains(key, StringComparer.OrdinalIgnoreCase))
            _keyOrder[section].Add(key);

        if (comment != null)
        {
            if (!_inlineComments.ContainsKey(section))
                _inlineComments[section] = new(StringComparer.OrdinalIgnoreCase);
            _inlineComments[section][key] = comment;
        }
    }

    public void SetInt(string section, string key, int value, string? comment = null)
        => SetString(section, key, value.ToString(CultureInfo.InvariantCulture), comment);

    public void SetDouble(string section, string key, double value, string? comment = null)
        => SetString(section, key, value.ToString("G", CultureInfo.InvariantCulture), comment);

    public void SetBool(string section, string key, bool value, string? comment = null)
        => SetString(section, key, value ? "true" : "false", comment);

    public void AddSectionComment(string section, string comment)
    {
        EnsureSection(section);
        if (!_sectionComments.ContainsKey(section))
            _sectionComments[section] = new();
        _sectionComments[section].Add(comment);
    }

    public void AddHeaderComment(string comment) => _headerComments.Add(comment);

    public void RemoveKey(string section, string key)
    {
        if (_sections.TryGetValue(section, out var dict))
        {
            dict.Remove(key);
            _keyOrder[section].RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Saving ──────────────────────────────────────────────────────────

    /// <summary>Save to the original file path (or a new path).</summary>
    public void Save(string? filePath = null)
    {
        filePath ??= _filePath;
        if (string.IsNullOrEmpty(filePath))
            throw new InvalidOperationException("No file path specified.");

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(filePath, ToString(), Encoding.UTF8);
        _filePath = filePath;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();

        // Header comments
        foreach (var comment in _headerComments)
            sb.AppendLine($"# {comment}");

        if (_headerComments.Count > 0)
            sb.AppendLine();

        // Sections
        foreach (var section in _sectionOrder)
        {
            // Section comments (above the [section] header)
            if (_sectionComments.TryGetValue(section, out var comments))
            {
                foreach (var comment in comments)
                    sb.AppendLine($"# {comment}");
            }

            sb.AppendLine($"[{section}]");

            if (_keyOrder.TryGetValue(section, out var keys) &&
                _sections.TryGetValue(section, out var dict))
            {
                foreach (var key in keys)
                {
                    if (dict.TryGetValue(key, out var value))
                    {
                        string line = $"{key}={value}";

                        // Add inline comment if present
                        if (_inlineComments.TryGetValue(section, out var inlineDict) &&
                            inlineDict.TryGetValue(key, out var inlineComment))
                        {
                            line += $"  ; {inlineComment}";
                        }

                        sb.AppendLine(line);
                    }
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Parsing ─────────────────────────────────────────────────────────

    private void Parse(string[] lines)
    {
        string currentSection = "General"; // Default section for keys before any [section]
        EnsureSection(currentSection);

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();

            // Empty line
            if (string.IsNullOrEmpty(line)) continue;

            // Comment line
            if (line.StartsWith('#') || line.StartsWith(';'))
            {
                string comment = line.TrimStart('#', ';', ' ');
                if (_sections[currentSection].Count == 0 && _sectionOrder.IndexOf(currentSection) == 0)
                    _headerComments.Add(comment);
                continue;
            }

            // Section header
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                EnsureSection(currentSection);
                continue;
            }

            // Key=Value pair
            int eqIndex = line.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = line.Substring(0, eqIndex).Trim();
                string valuePart = line.Substring(eqIndex + 1).Trim();

                // Handle inline comments (but not inside quoted values)
                string value = valuePart;
                string? inlineComment = null;

                if (!valuePart.StartsWith('"'))
                {
                    int commentIdx = valuePart.IndexOf(';');
                    if (commentIdx < 0) commentIdx = valuePart.IndexOf('#');

                    if (commentIdx > 0)
                    {
                        value = valuePart.Substring(0, commentIdx).Trim();
                        inlineComment = valuePart.Substring(commentIdx + 1).Trim();
                    }
                }
                else
                {
                    // Strip surrounding quotes
                    int closeQuote = valuePart.IndexOf('"', 1);
                    if (closeQuote > 0)
                        value = valuePart.Substring(1, closeQuote - 1);
                }

                _sections[currentSection][key] = value;
                if (!_keyOrder[currentSection].Contains(key, StringComparer.OrdinalIgnoreCase))
                    _keyOrder[currentSection].Add(key);

                if (inlineComment != null)
                {
                    if (!_inlineComments.ContainsKey(currentSection))
                        _inlineComments[currentSection] = new(StringComparer.OrdinalIgnoreCase);
                    _inlineComments[currentSection][key] = inlineComment;
                }
            }
        }
    }

    private void EnsureSection(string section)
    {
        if (!_sections.ContainsKey(section))
        {
            _sections[section] = new(StringComparer.OrdinalIgnoreCase);
            _keyOrder[section] = new();

            if (!_sectionOrder.Contains(section, StringComparer.OrdinalIgnoreCase))
                _sectionOrder.Add(section);
        }
    }
}
