namespace Csmx.Compiler;

public sealed class LineMap
{
    private readonly int[] _lineStarts;
    private readonly int _textLength;

    private LineMap(int[] lineStarts, int textLength)
    {
        _lineStarts = lineStarts;
        _textLength = textLength;
    }

    public static LineMap FromText(string text)
    {
        var starts = new List<int> { 0 };

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return new LineMap(starts.ToArray(), text.Length);
    }

    public (int Line, int Character) GetLinePosition(int index)
    {
        index = CsmxCompat.Clamp(index, 0, _textLength);

        var line = Array.BinarySearch(_lineStarts, index);
        if (line < 0)
        {
            line = ~line - 1;
        }

        line = CsmxCompat.Clamp(line, 0, _lineStarts.Length - 1);
        return (line, index - _lineStarts[line]);
    }

    public int GetIndex(int line, int character)
    {
        if (_lineStarts.Length == 0)
        {
            return 0;
        }

        line = CsmxCompat.Clamp(line, 0, _lineStarts.Length - 1);
        return CsmxCompat.Clamp(_lineStarts[line] + Math.Max(0, character), 0, _textLength);
    }
}
