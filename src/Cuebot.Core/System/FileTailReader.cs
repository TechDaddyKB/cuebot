using System.Text;

namespace Cuebot.Core.SystemTools;

/// <summary>
/// Reads lines appended to a text file since the last call (UTF-8). Partial lines wait for their newline;
/// a file that shrinks (truncated or rotated) is read again from the start. Polling keeps this portable and
/// reliable on network shares, where change notifications are not.
/// </summary>
public sealed class FileTailReader
{
    private readonly string _path;
    private readonly StringBuilder _partial = new();
    private Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private long _position = -1;
    private bool _startAtEnd;

    /// <param name="startAtEnd">Skip what the file already holds; when the file doesn't exist yet it is read from the start.</param>
    public FileTailReader(string path, bool startAtEnd = true)
    {
        _path = path;
        _startAtEnd = startAtEnd;
    }

    /// <summary>False while the file is missing.</summary>
    public bool FileExists { get; private set; }

    /// <summary>Complete lines appended since the previous call.</summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            FileExists = false;
            if (_position < 0) _startAtEnd = false; // it will be new when it appears
            else Reset();
            return [];
        }

        using (stream)
        {
            FileExists = true;
            var length = stream.Length;
            if (_position < 0)
            {
                _position = _startAtEnd ? length : 0;
            }
            else if (length < _position)
            {
                Reset();
                _position = 0;
            }
            if (length == _position) return [];

            stream.Seek(_position, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(length - _position, 4 * 1024 * 1024)];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            _position += read;
            var chars = new char[_decoder.GetCharCount(bytes, 0, read)];
            _decoder.GetChars(bytes, 0, read, chars, 0);
            _partial.Append(chars);
            return TakeLines();
        }
    }

    private void Reset()
    {
        _partial.Clear();
        _decoder = new UTF8Encoding(false).GetDecoder();
        _position = 0;
    }

    private List<string> TakeLines()
    {
        var lines = new List<string>();
        var text = _partial.ToString();
        var start = 0;
        int newline;
        while ((newline = text.IndexOf('\n', start)) >= 0)
        {
            var line = text[start..newline].TrimEnd('\r');
            if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            lines.Add(line);
            start = newline + 1;
        }
        _partial.Clear().Append(text, start, text.Length - start);
        return lines;
    }
}
