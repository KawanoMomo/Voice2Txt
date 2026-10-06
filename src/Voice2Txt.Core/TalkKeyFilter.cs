namespace Voice2Txt.Core;

/// <summary>キー 1 つの扱い。</summary>
public enum TalkKeySignal { None, TalkDown, TalkUp, OtherKeyWhileTalk }

/// <param name="Swallow">操作中のアプリへ渡さない(false なら素通し)。</param>
/// <param name="Signal">PushToTalkController へ伝えること。</param>
/// <param name="InjectDown">代わりに合成して送るキーの押下(仮想キーコード。順に)。</param>
public readonly record struct KeyVerdict(bool Swallow, TalkKeySignal Signal, int[] InjectDown)
{
    public static readonly KeyVerdict Pass = new(false, TalkKeySignal.None, []);
}

/// <summary>
/// トークキーの判定(キーボードフックと検証モードが共有する)。仮想キーコードで受ける。
/// トークキーは握りつぶし(リピートも)、押下中に別のキーが押されたら発話を取り消し、トークキーを修飾キーとして
/// 合成した押下に続けて同じキーを送る(ショートカットを効かせる)。以後トークキーを離すまでは素通し。それ以外は素通し。
/// </summary>
public sealed class TalkKeyFilter(int talkVk)
{
    private bool _talkDown, _passThrough;

    public int TalkVk => talkVk;

    public KeyVerdict OnKey(int vk, bool down)
    {
        if (vk == talkVk)
        {
            if (down)
            {
                if (_passThrough) return KeyVerdict.Pass;
                if (_talkDown) return new(true, TalkKeySignal.None, []);
                _talkDown = true;
                return new(true, TalkKeySignal.TalkDown, []);
            }
            _talkDown = false;
            if (_passThrough) { _passThrough = false; return KeyVerdict.Pass; }
            return new(true, TalkKeySignal.TalkUp, []);
        }
        if (down && _talkDown && !_passThrough)
        {
            _passThrough = true;
            return new(true, TalkKeySignal.OtherKeyWhileTalk, [talkVk, vk]);
        }
        return KeyVerdict.Pass;
    }
}
