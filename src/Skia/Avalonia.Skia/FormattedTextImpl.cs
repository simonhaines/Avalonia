using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Utilities;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia formatted text implementation.
    /// </summary>
    internal class FormattedTextImpl : IFormattedTextImpl
    {
        private static readonly ThreadLocal<SKTextBlobBuilder> t_builder = new ThreadLocal<SKTextBlobBuilder>(() => new SKTextBlobBuilder());

        private const float MAX_LINE_WIDTH = 10000;

        private readonly List<KeyValuePair<FBrushRange, IBrush>> _foregroundBrushes =
                                                new List<KeyValuePair<FBrushRange, IBrush>>();
        private readonly List<FormattedTextLine> _lines = new List<FormattedTextLine>();
        private readonly SKPaint _paint;
        private readonly List<Rect> _rects = new List<Rect>();
        public string Text { get; }
        private readonly TextWrapping _wrapping;
        private Size _constraint = new Size(double.PositiveInfinity, double.PositiveInfinity);
        private float _lineHeight = 0;
        private float _lineOffset = 0;
        private Rect _bounds;
        private List<AvaloniaFormattedTextLine> _skiaLines;
        private ReadOnlySlice<ushort> _glyphs;
        private ReadOnlySlice<float> _advances;

        public FormattedTextImpl(
            string text,
            Typeface typeface,
            double fontSize,
            TextAlignment textAlignment,
            TextWrapping wrapping,
            Size constraint,
            IReadOnlyList<FormattedTextStyleSpan> spans)
        {
            Text = text ?? string.Empty;

            UpdateGlyphInfo(Text, typeface.GlyphTypeface, (float)fontSize);
         
            _paint = new SKPaint
            {
                TextEncoding = SKTextEncoding.Utf16,
                IsStroke = false,
                IsAntialias = true,
                LcdRenderText = true,
                SubpixelText = true,
                IsLinearText = true,
                Typeface = ((GlyphTypefaceImpl)typeface.GlyphTypeface.PlatformImpl).Typeface,
                TextSize = (float)fontSize,
                TextAlign = textAlignment.ToSKTextAlign()
            };

            //currently Skia does not measure properly with Utf8 !!!
            //Paint.TextEncoding = SKTextEncoding.Utf8;

            _wrapping = wrapping;
            _constraint = constraint;

            if (spans != null)
            {
                foreach (var span in spans)
                {
                    if (span.ForegroundBrush != null)
                    {
                        SetForegroundBrush(span.ForegroundBrush, span.StartIndex, span.Length);
                    }
                }
            }

            Rebuild();
        }

        public Size Constraint => _constraint;

        public Rect Bounds => _bounds;

        public IEnumerable<FormattedTextLine> GetLines()
        {
            return _lines;
        }

        public TextHitTestResult HitTestPoint(Point point)
        {
            float y = (float)point.Y;

            AvaloniaFormattedTextLine line = default;

            float nextTop = 0;

            foreach(var currentLine in _skiaLines)
            {
                if(currentLine.Top <= y)
                {
                    line = currentLine;
                    nextTop = currentLine.Top + currentLine.Height;
                }
                else
                {
                    nextTop = currentLine.Top;
                    break;
                }
            }

            if (!line.Equals(default(AvaloniaFormattedTextLine)))
            {
                var rects = GetRects();

                for (int c = line.Start; c < line.Start + line.TextLength; c++)
                {
                    var rc = rects[c];
                    if (rc.Contains(point))
                    {
                        return new TextHitTestResult
                        {
                            IsInside = !(line.TextLength > line.Length),
                            TextPosition = c,
                            IsTrailing = (point.X - rc.X) > rc.Width / 2
                        };
                    }
                }

                int offset = 0;

                if (point.X >= (rects[line.Start].X + line.Width) && line.Length > 0)
                {
                    offset = line.TextLength > line.Length ?
                                    line.Length : (line.Length - 1);
                }

                if (y < nextTop)
                {
                    return new TextHitTestResult
                    {
                        IsInside = false,
                        TextPosition = line.Start + offset,
                        IsTrailing = Text.Length == (line.Start + offset + 1)
                    };
                }
            }

            bool end = point.X > _bounds.Width || point.Y > _lines.Sum(l => l.Height);

            return new TextHitTestResult()
            {
                IsInside = false,
                IsTrailing = end,
                TextPosition = end ? Text.Length - 1 : 0
            };
        }

        public Rect HitTestTextPosition(int index)
        {
            if (string.IsNullOrEmpty(Text))
            {
                var alignmentOffset = TransformX(0, 0, _paint.TextAlign);
                return new Rect(alignmentOffset, 0, 0, _lineHeight);
            }
            var rects = GetRects();
            if (index >= Text.Length || index < 0)
            {
                var r = rects.LastOrDefault();

                var c = Text[Text.Length - 1];

                switch (c)
                {
                    case '\n':
                    case '\r':
                        return new Rect(r.X, r.Y, 0, _lineHeight);
                    default:
                        return new Rect(r.X + r.Width, r.Y, 0, _lineHeight);
                }
            }
            return rects[index];
        }

        public IEnumerable<Rect> HitTestTextRange(int index, int length)
        {
            List<Rect> result = new List<Rect>();

            var rects = GetRects();

            int lastIndex = index + length - 1;

            foreach (var line in _skiaLines.Where(l =>
                                                    (l.Start + l.Length) > index &&
                                                    lastIndex >= l.Start &&
                                                    !l.IsEmptyTrailingLine))
            {
                int lineEndIndex = line.Start + (line.Length > 0 ? line.Length - 1 : 0);

                double left = rects[line.Start > index ? line.Start : index].X;
                double right = rects[lineEndIndex > lastIndex ? lastIndex : lineEndIndex].Right;

                result.Add(new Rect(left, line.Top, right - left, line.Height));
            }

            return result;
        }

        public override string ToString()
        {
            return Text;
        }

        private void DrawTextBlob(int start, int length, float x, float y, SKCanvas canvas, SKPaint paint)
        {
            if(length == 0)
            {
                return;
            }

            var glyphs = _glyphs.Buffer.Span.Slice(start, length);
            var advances = _advances.Buffer.Span.Slice(start, length);
            var builder = t_builder.Value;

            var buffer = builder.AllocateHorizontalRun(_paint.ToFont(), length, 0);

            buffer.SetGlyphs(glyphs);

            var positions = buffer.GetPositionSpan();

            var pos = 0f;

            for (int i = 0; i < advances.Length; i++)
            {
                positions[i] = pos;

                pos += advances[i];
            }

            var blob = builder.Build();

            if(blob != null)
            {
                canvas.DrawText(blob, x, y, paint);
            }
        }
        
        internal void Draw(DrawingContextImpl context,
            SKCanvas canvas,
            SKPoint origin,
            DrawingContextImpl.PaintWrapper foreground,
            bool canUseLcdRendering)
        {
            /* TODO: This originated from Native code, it might be useful for debugging character positions as
             * we improve the FormattedText support. Will need to port this to C# obviously. Rmove when
             * not needed anymore.

                SkPaint dpaint;
                ctx->Canvas->save();
                ctx->Canvas->translate(origin.fX, origin.fY);
                for (int c = 0; c < Lines.size(); c++)
                {
                    dpaint.setARGB(255, 0, 0, 0);
                    SkRect rc;
                    rc.fLeft = 0;
                    rc.fTop = Lines[c].Top;
                    rc.fRight = Lines[c].Width;
                    rc.fBottom = rc.fTop + LineOffset;
                    ctx->Canvas->drawRect(rc, dpaint);
                }
                for (int c = 0; c < Length; c++)
                {
                    dpaint.setARGB(255, c % 10 * 125 / 10 + 125, (c * 7) % 10 * 250 / 10, (c * 13) % 10 * 250 / 10);
                    dpaint.setStyle(SkPaint::kFill_Style);
                    ctx->Canvas->drawRect(Rects[c], dpaint);
                }
                ctx->Canvas->restore();
            */
            using (var paint = _paint.Clone())
            {
                IDisposable currd = null;
                var currentWrapper = foreground;
                SKPaint currentPaint = null;
                try
                {
                    ApplyWrapperTo(ref currentPaint, foreground, ref currd, paint, canUseLcdRendering);
                    bool hasCusomFGBrushes = _foregroundBrushes.Any();

                    for (int c = 0; c < _skiaLines.Count; c++)
                    {
                        AvaloniaFormattedTextLine line = _skiaLines[c];

                        float x = TransformX(origin.X, 0, paint.TextAlign);

                        if (!hasCusomFGBrushes)
                        {
                            DrawTextBlob(line.Start, line.Length, x, origin.Y + line.Top + _lineOffset, canvas, paint);
                        }
                        else
                        {
                            float currX = x;
                            float measure;
                            int len;
                            float factor;

                            switch (paint.TextAlign)
                            {
                                case SKTextAlign.Left:
                                    factor = 0;
                                    break;
                                case SKTextAlign.Center:
                                    factor = 0.5f;
                                    break;
                                case SKTextAlign.Right:
                                    factor = 1;
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }

                            currX -= line.Length == 0 ? 0 : MeasureText(line.Start, line.Length) * factor;

                            for (int i = line.Start; i < line.Start + line.Length;)
                            {
                                var fb = GetNextForegroundBrush(ref line, i, out len);

                                if (fb != null)
                                {
                                    //TODO: figure out how to get the brush size
                                    currentWrapper = context.CreatePaint(new SKPaint { IsAntialias = true }, fb,
                                        new Size());
                                }
                                else
                                {
                                    if (!currentWrapper.Equals(foreground)) currentWrapper.Dispose();
                                    currentWrapper = foreground;
                                }

                                measure = MeasureText(i, len);
                                currX += measure * factor;

                                ApplyWrapperTo(ref currentPaint, currentWrapper, ref currd, paint, canUseLcdRendering);    

                                DrawTextBlob(i, len, currX, origin.Y + line.Top + _lineOffset, canvas, paint);

                                i += len;
                                currX += measure * (1 - factor);
                            }
                        }
                    }
                }
                finally
                {
                    if (!currentWrapper.Equals(foreground)) currentWrapper.Dispose();
                    currd?.Dispose();
                }
            }
        }

        private static void ApplyWrapperTo(ref SKPaint current, DrawingContextImpl.PaintWrapper wrapper,
                                                ref IDisposable curr, SKPaint paint, bool canUseLcdRendering)
        {
            if (current == wrapper.Paint)
                return;
            curr?.Dispose();
            curr = wrapper.ApplyTo(paint);
            paint.LcdRenderText = canUseLcdRendering;
        }

        private static bool IsBreakChar(char c)
        {
            //white space or zero space whitespace
            return char.IsWhiteSpace(c) || c == '\u200B';
        }

        private static int LineBreak(string textInput, int textIndex, int stop,
                                     SKPaint paint, float maxWidth,
                                     out int trailingCount)
        {
            int lengthBreak;
            if (maxWidth == -1)
            {
                lengthBreak = stop - textIndex;
            }
            else
            {
                string subText = textInput.Substring(textIndex, stop - textIndex);
                lengthBreak = (int)paint.BreakText(subText, maxWidth, out _);
            }

            //Check for white space or line breakers before the lengthBreak
            int startIndex = textIndex;
            int index = textIndex;
            int word_start = textIndex;
            bool prevBreak = true;

            trailingCount = 0;

            while (index < stop)
            {
                int prevText = index;
                char currChar = textInput[index++];
                bool currBreak = IsBreakChar(currChar);

                if (!currBreak && prevBreak)
                {
                    word_start = prevText;
                }

                prevBreak = currBreak;

                if (index > startIndex + lengthBreak)
                {
                    if (currBreak)
                    {
                        // eat the rest of the whitespace
                        while (index < stop && IsBreakChar(textInput[index]))
                        {
                            index++;
                        }

                        trailingCount = index - prevText;
                    }
                    else
                    {
                        // backup until a whitespace (or 1 char)
                        if (word_start == startIndex)
                        {
                            if (prevText > startIndex)
                            {
                                index = prevText;
                            }
                        }
                        else
                        {
                            index = word_start;
                        }
                    }
                    break;
                }

                if ('\n' == currChar)
                {
                    int ret = index - startIndex;
                    int lineBreakSize = 1;
                    if (index < stop)
                    {
                        currChar = textInput[index++];
                        if ('\r' == currChar)
                        {
                            ret = index - startIndex;
                            ++lineBreakSize;
                        }
                    }

                    trailingCount = lineBreakSize;

                    return ret;
                }

                if ('\r' == currChar)
                {
                    int ret = index - startIndex;
                    int lineBreakSize = 1;
                    if (index < stop)
                    {
                        currChar = textInput[index++];
                        if ('\n' == currChar)
                        {
                            ret = index - startIndex;
                            ++lineBreakSize;
                        }
                    }

                    trailingCount = lineBreakSize;

                    return ret;
                }
            }

            return index - startIndex;
        }

        private void BuildRects()
        {
            // Build character rects
            SKTextAlign align = _paint.TextAlign;

            for (int li = 0; li < _skiaLines.Count; li++)
            {
                var line = _skiaLines[li];
                float prevRight = TransformX(0, line.Width, align);
                double nextTop = line.Top + line.Height;

                if (li + 1 < _skiaLines.Count)
                {
                    nextTop = _skiaLines[li + 1].Top;
                }

                for (int i = line.Start; i < line.Start + line.TextLength; i++)
                {
                    var w = line.IsEmptyTrailingLine ? 0 : _advances[i];

                    _rects.Add(new Rect(
                        prevRight,
                        line.Top,
                        w,
                        nextTop - line.Top));
                    prevRight += w;
                }
            }
        }

        private IBrush GetNextForegroundBrush(ref AvaloniaFormattedTextLine line, int index, out int length)
        {
            IBrush result = null;
            int len = length = line.Start + line.Length - index;

            if (_foregroundBrushes.Any())
            {
                var bi = _foregroundBrushes.FindIndex(b =>
                                                        b.Key.StartIndex <= index &&
                                                        b.Key.EndIndex > index
                                                        );

                if (bi > -1)
                {
                    var match = _foregroundBrushes[bi];

                    len = match.Key.EndIndex - index;
                    result = match.Value;

                    if (len > 0 && len < length)
                    {
                        length = len;
                    }
                }

                int endIndex = index + length;
                int max = bi == -1 ? _foregroundBrushes.Count : bi;
                var next = _foregroundBrushes.Take(max)
                                                .Where(b => b.Key.StartIndex < endIndex &&
                                                            b.Key.StartIndex > index)
                                                .OrderBy(b => b.Key.StartIndex)
                                                .FirstOrDefault();

                if (next.Value != null)
                {
                    length = next.Key.StartIndex - index;
                }
            }

            return result;
        }

        private List<Rect> GetRects()
        {
            if (Text.Length > _rects.Count)
            {
                BuildRects();
            }

            return _rects;
        }

        private void Rebuild()
        {
            var length = Text.Length;

            _lines.Clear();
            _rects.Clear();
            _skiaLines = new List<AvaloniaFormattedTextLine>();

            int curOff = 0;
            float curY = 0;

            var metrics = _paint.FontMetrics;
            var mTop = metrics.Top;  // The greatest distance above the baseline for any glyph (will be <= 0).
            var mBottom = metrics.Bottom;  // The greatest distance below the baseline for any glyph (will be >= 0).
            var mLeading = metrics.Leading;  // The recommended distance to add between lines of text (will be >= 0).
            var mDescent = metrics.Descent;  //The recommended distance below the baseline. Will be >= 0.
            var mAscent = metrics.Ascent;    //The recommended distance above the baseline. Will be <= 0.
            var lastLineDescent = mBottom - mDescent;

            // This seems like the best measure of full vertical extent
            // matches Direct2D line height
            _lineHeight = mDescent - mAscent + metrics.Leading;

            // Rendering is relative to baseline
            _lineOffset = (-metrics.Ascent);

            string subString;

            float widthConstraint = double.IsPositiveInfinity(_constraint.Width)
                                        ? -1
                                        : (float)_constraint.Width;
            
            while(curOff < length)
            {
                float lineWidth = -1;
                int measured;
                int trailingnumber = 0;
                
                float constraint = -1;

                if (_wrapping == TextWrapping.Wrap)
                {
                    constraint = widthConstraint <= 0 ? MAX_LINE_WIDTH : widthConstraint;
                    if (constraint > MAX_LINE_WIDTH)
                        constraint = MAX_LINE_WIDTH;
                }

                measured = LineBreak(Text, curOff, length, _paint, constraint, out trailingnumber);
                AvaloniaFormattedTextLine line = new AvaloniaFormattedTextLine();
                line.Start = curOff;
                line.TextLength = measured;
                subString = Text.Substring(line.Start, line.TextLength);
                lineWidth = MeasureText(line.Start, line.TextLength);
                line.Length = measured - trailingnumber;
                line.Width = lineWidth;
                line.Height = _lineHeight;
                line.Top = curY;

                _skiaLines.Add(line);

                curY += _lineHeight;
                curY += mLeading;
                curOff += measured;

                //if this is the last line and there are trailing newline characters then
                //insert a additional line
                if (curOff >= length)
                {
                    var subStringMinusNewlines = subString.TrimEnd('\n', '\r');
                    var lengthDiff = subString.Length - subStringMinusNewlines.Length;
                    if (lengthDiff > 0)
                    {
                        AvaloniaFormattedTextLine lastLine = new AvaloniaFormattedTextLine();
                        lastLine.TextLength = lengthDiff;
                        lastLine.Start = curOff - lengthDiff;
                        var lastLineWidth = MeasureText(line.Start, line.TextLength);
                        lastLine.Length = 0;
                        lastLine.Width = lastLineWidth;
                        lastLine.Height = _lineHeight;
                        lastLine.Top = curY;
                        lastLine.IsEmptyTrailingLine = true;

                        _skiaLines.Add(lastLine);

                        curY += _lineHeight;
                        curY += mLeading;
                    }
                }
            }

            // Now convert to Avalonia data formats
            _lines.Clear();
            float maxX = 0;

            for (var c = 0; c < _skiaLines.Count; c++)
            {
                var w = _skiaLines[c].Width;
                if (maxX < w)
                    maxX = w;

                _lines.Add(new FormattedTextLine(_skiaLines[c].TextLength, _skiaLines[c].Height));
            }

            if (_skiaLines.Count == 0)
            {
                _lines.Add(new FormattedTextLine(0, _lineHeight));
                _bounds = new Rect(0, 0, 0, _lineHeight);
            }
            else
            {
                var lastLine = _skiaLines[_skiaLines.Count - 1];
                _bounds = new Rect(0, 0, maxX, lastLine.Top + lastLine.Height);

                if (double.IsPositiveInfinity(Constraint.Width))
                {
                    return;
                }

                switch (_paint.TextAlign)
                {
                    case SKTextAlign.Center:
                        _bounds = new Rect(Constraint).CenterRect(_bounds);
                        break;
                    case SKTextAlign.Right:
                        _bounds = new Rect(
                            Constraint.Width - _bounds.Width,
                            0,
                            _bounds.Width,
                            _bounds.Height);
                        break;
                }
            }
        }

        private float MeasureText(int start, int length)
        {
            var width = 0f;

            for (int i = start; i < start + length; i++)
            {
                var advance = _advances[i];

                width += advance;
            }

            return width;
        }

        private void UpdateGlyphInfo(string text, GlyphTypeface glyphTypeface, float fontSize)
        {
            var glyphs = new ushort[text.Length];
            var advances = new float[text.Length];

            var scale = fontSize / glyphTypeface.DesignEmHeight;
            var width = 0f;
            var characters = text.AsSpan();

            for (int i = 0; i < characters.Length; i++)
            {
                var c = characters[i];
                float advance;
                ushort glyph;

                switch (c)
                {
                    case (char)0:
                        {
                            glyph = glyphTypeface.GetGlyph(0x200B);
                            advance = 0;
                            break;
                        }
                    case '\t':
                        {
                            glyph = glyphTypeface.GetGlyph(' ');
                            advance = glyphTypeface.GetGlyphAdvance(glyph) * scale * 4;
                            break;
                        }
                    default:
                        {
                            glyph = glyphTypeface.GetGlyph(c);
                            advance = glyphTypeface.GetGlyphAdvance(glyph) * scale;
                            break;
                        }
                }

                glyphs[i] = glyph;
                advances[i] = advance;

                width += advance;
            }

            _glyphs = new ReadOnlySlice<ushort>(glyphs);
            _advances = new ReadOnlySlice<float>(advances);
        }

        private float TransformX(float originX, float lineWidth, SKTextAlign align)
        {
            float x = 0;

            if (align == SKTextAlign.Left)
            {
                x = originX;
            }
            else
            {
                double width = Constraint.Width > 0 && !double.IsPositiveInfinity(Constraint.Width) ?
                                Constraint.Width :
                                _bounds.Width;

                switch (align)
                {
                    case SKTextAlign.Center: x = originX + (float)(width - lineWidth) / 2; break;
                    case SKTextAlign.Right: x = originX + (float)(width - lineWidth); break;
                }
            }

            return x;
        }

        private void SetForegroundBrush(IBrush brush, int startIndex, int length)
        {
            var key = new FBrushRange(startIndex, length);
            int index = _foregroundBrushes.FindIndex(v => v.Key.Equals(key));

            if (index > -1)
            {
                _foregroundBrushes.RemoveAt(index);
            }

            if (brush != null)
            {
                brush = brush.ToImmutable();
                _foregroundBrushes.Insert(0, new KeyValuePair<FBrushRange, IBrush>(key, brush));
            }
        }

        private struct AvaloniaFormattedTextLine
        {
            public float Height;
            public int Length;
            public int Start;
            public int TextLength;
            public float Top;
            public float Width;
            public bool IsEmptyTrailingLine;
        };

        private struct FBrushRange
        {
            public FBrushRange(int startIndex, int length)
            {
                StartIndex = startIndex;
                Length = length;
            }

            public int EndIndex => StartIndex + Length;

            public int Length { get; private set; }

            public int StartIndex { get; private set; }

            public bool Intersects(int index, int len) =>
                (index + len) > StartIndex &&
                (StartIndex + Length) > index;

            public override string ToString()
            {
                return $"{StartIndex}-{EndIndex}";
            }
        }
    }
}
