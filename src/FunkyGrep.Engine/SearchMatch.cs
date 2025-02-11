﻿using System;
using System.Collections.Generic;

namespace FunkyGrep.Engine;

public class SearchMatch
{
    public int LineNumber { get; }
    public IReadOnlyList<string> PreMatchLines { get; }
    public string Line { get; }
    public IReadOnlyList<string> PostMatchLines { get; }
    public int MatchIndex { get; }
    public int MatchLength { get; }

    public SearchMatch(
        int matchedLineNumber,
        string matchedLine,
        int matchIndex,
        int matchLength,
        IReadOnlyList<string>? preMatchLines,
        IReadOnlyList<string>? postMatchLines)
    {
        if (matchedLineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(matchedLineNumber));
        }

        this.LineNumber = matchedLineNumber;
        this.Line = matchedLine;
        this.MatchIndex = matchIndex;
        this.MatchLength = matchLength;
        this.PreMatchLines = preMatchLines ?? Array.Empty<string>();
        this.PostMatchLines = postMatchLines ?? Array.Empty<string>();
    }
}
