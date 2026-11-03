namespace Amberline.Core;

/// <summary>
/// The two colours of <c>ChangePARColorOverride</c> (<c>0x99</c>): the bar and its fade, each an <c>r3dColor</c>, which is four bytes in the
/// order blue, green, red, alpha, so a little-endian <see cref="uint"/> of <c>b | g &lt;&lt; 8 | r &lt;&lt; 16 | a &lt;&lt; 24</c> is the wire form.
/// </summary>
public readonly record struct ParColorOverride(uint Bar, uint Fade)
{
    public static uint Pack(byte r, byte g, byte b, byte a) => b | (uint)g << 8 | (uint)r << 16 | (uint)a << 24;
}
