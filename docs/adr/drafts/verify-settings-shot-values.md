# ADR-draft: 検証モードの設定画面のスクショは、選択式の値が写ったかを確かめてから残す

- **ステータス**: 提案(未採番)
- **カテゴリ**: architecture
- **日付**: 2026-11-03
- **対象プロジェクト**: Voice2Txt
- **関連**: verify-screenshot-own-overlay-only.md(オーバーレイの写しを自前の描画と照合する)、settings-screen-from-schema.md、BLK-owner-20261103-0635-3

## コンテキスト

検証モードの `openSettings` / `saveSettings` は設定画面を `DrawToBitmap`(部品に自分を描かせる render)で撮る。
同じ exe・同じ台本でも、選択式の 5 項目(ドロップダウン)が空欄に写る時間帯があった(06:31〜06:37 は全部空欄、05:50 と 07:10 以降は写る)。再現条件は掴めていない。
画面から写すと、開いた直後は窓の開く動き(DWM の浮き上がり)の途中で、部品が 10 px 以上ずれて写った。

## 検討した選択肢

1. 常に画面から写す — 並行して走る他の検証モードの窓・オーバーレイ・タスクバーが重なる。開く動きの途中だとずれる
2. 部品を自前で描く(owner draw に変える)— 製品の設定画面の描き方を変えることになる
3. **render の後、選択式の欄に文字の色があるかを確かめる。空なら描き直させて撮り直し、それでも空なら空欄の部品だけを画面から写して差し込み(その部品の上に別の窓が無いときだけ)、画面にも無ければ値を書き足す。どれをしたかを capture と filled に残す**

## 決定

3 を採る。判定は `Voice2Txt.Core.Verification.SettingsCapture`、撮るのは `SettingsForm.SaveScreenshot`。検証モードは設定画面の開く動きを止める(`DWMWA_TRANSITIONS_FORCEDISABLED`)。
撮った時点で画面に出ている各項目の値(設定ファイルの形)を result.json の `shots[].values` に残し、台本は `expect.shots[].values` で確かめる。

## 結果

- 普段は従来どおり `capture: "render"`。空欄だったときは `render (render left blank: …; from screen: …)` か `…; filled by verify: …` になり、黙って写ったことにしない
- 製品の設定画面(項目・文言・並び・描き方)は変えない
