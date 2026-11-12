# RAR の見本

RAR を作るツールは、ライセンスの都合で Expzip のリポジトリでは使わない。ここの RAR は、SharpCompress のテスト用の書庫を写したもの (#336)。

- 出どころ: https://github.com/adamhathcock/sharpcompress の `tests/TestArchives/Archives` (タグ 0.50.8、コミット ef3a923211d6043a3b8d95c96b2808516866efc5)
- ライセンス: SharpCompress と同じ MIT License (Copyright (c) 2014 Adam Hathcock)
- 名前は元のまま。中身は変えていない

攻撃の見本 (辞書の大きさ、見出しの数、リンクなど) は、テストの中で RAR5 の見出しを組み立てて作る (`RarSample.cs`)。
