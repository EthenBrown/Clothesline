using System;

namespace Clothesline;

/// <summary>
/// 认出「同一次截图被两条路径各收了一遍」。
/// 一次截图通常既写剪贴板、又可能落盘，剪贴板本身还会连发好几条通知；
/// 这里用「真实像素尺寸 + 内容指纹 + 时间窗」把它们归成一次。
/// 纯逻辑、不碰 WPF，便于单独跑回归测试。
/// </summary>
internal sealed class ShotDedup
{
    private sealed record Hang(DateTime At, int W, int H, string Fingerprint, bool FromClipboard);

    private Hang? _last;

    /// <summary>同来源 2 秒、跨来源 6 秒；尺寸必须一致；两边都有指纹时必须一致。</summary>
    public bool ShouldSkip(int w, int h, string fingerprint, bool fromClipboard, DateTime now)
    {
        if (_last is not { } last) return false;

        double window = last.FromClipboard == fromClipboard ? 2.0 : 6.0;
        if ((now - last.At).TotalSeconds >= window) return false;
        if (last.W != w || last.H != h) return false;

        // 指纹都算出来了就以指纹为准，避免「连续截两张同尺寸但内容不同」被误吞。
        if (fingerprint.Length > 0 && last.Fingerprint.Length > 0)
            return fingerprint == last.Fingerprint;

        return true; // 指纹取不到时退回尺寸判定
    }

    public void Mark(int w, int h, string fingerprint, bool fromClipboard, DateTime now)
        => _last = new Hang(now, w, h, fingerprint ?? string.Empty, fromClipboard);

    public void Reset() => _last = null;
}
