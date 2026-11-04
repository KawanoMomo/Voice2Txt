# ADR-draft: 検証モードのスクショに別の実行のオーバーレイが写る — 画面の写しを自前の描画と照合する

- **ステータス**: 提案(未採番)
- **カテゴリ**: architecture
- **日付**: 2026-10-07
- **対象プロジェクト**: Voice2Txt
- **関連**: ADR-draft foundation-backend-fallback-and-verify-boundaries(オーバーレイは画面から CAPTUREBLT で写し、写せなければ同じ描画で代える)、BLK-junior-20261007-1743

## コンテキスト

検証モードはオーバーレイを画面の DC から自分の Bounds で BitBlt して撮る。ループではペルソナと builder が検証モードを並行して回すので、
同じ位置(画面下中央)に別の実行のオーバーレイが後から出ると、それが最前面になり自分の枠で写る。
「処理中」「貼り付け完了」のスクショに別の実行の「録音中」が写り、result.json の文言と画像が合わなかった。実機は多重起動しないので製品の表示の不具合ではない。

## 検討した選択肢

1. 常に自前の描画(PaintContent)で撮る — 並行でも必ず自分が写るが、実機の画面(不透明度・角丸・下地)の写しではなくなる
2. z 順で自分より上の窓を調べ、範囲に重なる別プロセスの窓があれば自前の描画に代える — 透明な最前面の窓(画面全体を覆うシステムの重ね合わせ等)でも重なりと判定してしまう
3. 画面の写しと自前の描画を角丸の内側で比べ、大きく食い違う画素(R/G/B のどれかが 96 超)が 2% を超えたら自前の描画に代える。上の窓は理由の説明にだけ使う

## 決定

3 を採る。`Voice2Txt.Core.Verification.OverlayCapture` が判定し、`OverlayForm.SaveScreenshot` が使う。
代えたときは capture を `render (overlapped: N% differs from own drawing; above: プロセス名#pid …)` とし、黙って `screen` にしない。
result の warnings(利用者に知らせた警告)には入れない(並行の有無で台本の期待 `warnings: []` が揺れるため)。
追記(BLK-owner-20261104-0320-2): 途中経過 3 行が窓の大半を占めると、案内の行が丸ごと別の実行の文言でも全体の食い違いは 1% 程度で閾値を下回った。
そこで案内の行(`OverlayLayout.RowTop` から下)だけの食い違いも同じ閾値で比べ、さらに z 順で上に同じ製品(同じプロセス名)の窓が重なっていれば画素によらず自前の描画に代える
(`OverlayCapture.RejectReason`。ログ `capture-check mismatch= row= above=`、理由 `overlapped: hint row N% …` / `overlapped: another Voice2Txt window above`)。
選択肢 2 の欠点(透明なシステムの重ね合わせ)は、同じ製品の窓に限ることで避ける。

## 結果

- 単独実行では食い違い 0.0000 で従来どおり `screen`。3 本並行(junior-3・junior-5・primary-3)では重なった状態だけが `render (overlapped: 14〜22% …)` になり、画像は自分の文言が読める
- 同じ幅・同じ文言の別の表示が完全に重なった場合は見分けない(写っている画は自分の表示と同じ)
