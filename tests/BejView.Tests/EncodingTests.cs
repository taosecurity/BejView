using System.Text;
using BejView.Core.Text;

namespace BejView.Tests;

public class EncodingTests
{
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, TextEncodingKind.Utf8, 3)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0 }, TextEncodingKind.Utf16LE, 2)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a' }, TextEncodingKind.Utf16BE, 2)]
    [InlineData(new byte[] { }, TextEncodingKind.Utf8, 0)]
    public void Byte_order_marks_are_recognised(byte[] head, TextEncodingKind kind, int bom)
    {
        var e = TextEncoding.Detect(head);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(bom, e.BomLength);
    }

    [Fact]
    public void UTF16_without_a_BOM_is_recognised_from_its_zero_bytes()
    {
        Assert.Equal(TextEncodingKind.Utf16LE, TextEncoding.Detect(Encoding.Unicode.GetBytes("Hello, log file\r\n")).Kind);
        Assert.Equal(TextEncodingKind.Utf16BE, TextEncoding.Detect(Encoding.BigEndianUnicode.GetBytes("Hello, log file\r\n")).Kind);
    }

    [Fact]
    public void Valid_UTF8_is_UTF8_and_other_bytes_are_Latin1()
    {
        Assert.Equal(TextEncodingKind.Utf8, TextEncoding.Detect(Encoding.UTF8.GetBytes("café ☕ plain")).Kind);
        Assert.Equal(TextEncodingKind.Latin1, TextEncoding.Detect(Encoding.Latin1.GetBytes("café crème brûlée")).Kind);
    }

    [Fact]
    public void A_sample_cut_in_the_middle_of_a_character_is_still_UTF8()
    {
        byte[] b = Encoding.UTF8.GetBytes("abc€");
        Assert.Equal(TextEncodingKind.Utf8, TextEncoding.Detect(b.AsSpan(0, b.Length - 1)).Kind);
    }

    [Theory]
    [InlineData(TextEncodingKind.Utf8)]
    [InlineData(TextEncodingKind.Utf16LE)]
    [InlineData(TextEncodingKind.Utf16BE)]
    [InlineData(TextEncodingKind.Latin1)]
    public void Newlines_are_found_and_counted_for_each_encoding(TextEncodingKind kind)
    {
        var m = new Model("a\nbé\r\n\nlast", kind, bom: false);
        var e = new TextEncoding(kind, 0, "test");
        Assert.Equal(3, e.CountNewlines(m.Bytes));
        Assert.Equal(m.Starts[1] - e.NewlineLength, e.IndexOfNewline(m.Bytes));
    }

    [Fact]
    public void UTF16_ignores_0x0A_bytes_that_are_half_of_another_character()
    {
        // U+0A0A is not a line break, though both its bytes are 0x0A (1.0 split lines here).
        byte[] b = Encoding.Unicode.GetBytes("ਊਊx");
        var e = new TextEncoding(TextEncodingKind.Utf16LE, 0, "test");
        Assert.Equal(-1, e.IndexOfNewline(b));
        Assert.Equal(0, e.CountNewlines(b));
    }

    [Fact]
    public void AlignBack_moves_a_cut_to_the_start_of_a_UTF8_character()
    {
        byte[] b = Encoding.UTF8.GetBytes("a€b"); // a, E2 82 AC, b
        var e = new TextEncoding(TextEncodingKind.Utf8, 0, "test");
        Assert.Equal(1, e.AlignBack(b, 1));
        Assert.Equal(1, e.AlignBack(b, 2));
        Assert.Equal(1, e.AlignBack(b, 3));
        Assert.Equal(4, e.AlignBack(b, 4));
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("ab€", 5)]
    public void Utf8CompleteLength_drops_an_unfinished_character(string s, int expected)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        Assert.Equal(expected, TextEncoding.Utf8CompleteLength(b));
        if (b.Length > 3) Assert.Equal(2, TextEncoding.Utf8CompleteLength(b.AsSpan(0, b.Length - 1)));
    }
}
