# 2026-11-01 PuddingCoreTests 跑不完：**一个测试挂住**，不是套件慢

## 现象

`dotnet test --no-build` 跑 20 分钟无任何结果，只能取消。构建本身 0 error。

## 定位（一次就中）

用 `--blame-hang` 代替人工二分：

```
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-build \
  --blame-hang --blame-hang-timeout 120s
```

120 秒后框架自行中止，并在 hang dump 里点名**当时仍在运行的测试**：

```
PuddingCoreTests.VisionPreprocessingWireTests.BodyBudgetIncludesTextAndRecompressesBeforeSending
（dump 里连续出现 3 次，即挂在这个方法上）
dump: Source\PuddingCoreTests\TestResults\<run>\testhost_*_hangdump.dmp
Seq : Source\PuddingCoreTests\TestResults\<run>\Sequence_*.xml
```

## 其余部分是绿的

同一次运行的 TRX：

```
results = 1130
  Passed      1128
  NotExecuted    2   （ProductionCountTokens_MatchesIndependentUnicodeVectors
                      WarmCompactionMeasurement_HandlesEmojiInLongContentAndReasoning）
  Failed         0
```

⇒ **整套 Core 测试只有这一个测试会挂死**，其余 1128 条全部通过、0 失败。

这解释了为什么「整包跑不完」：不是慢，是这一个永不返回。

## 影响

这个工程**无法被任何门禁覆盖** —— 只要跑它就会挂住。这和 MemoryEngineTests 长期变红是同一类结构性问题：**看不见 = 不设防**。

## 下一步（未做）

1. 读 `VisionPreprocessingWireTests.BodyBudgetIncludesTextAndRecompressesBeforeSending`，找出它等待的东西（未完成的 Task？没有信号量的等待？重试循环？）；
2. 判定是「测试本身死等」还是「生产代码在该路径下不返回」—— 后者就是真缺陷；
3. 修好后把 Core 测试纳入可跑集合。

## 可复用的一条

**怀疑「测试挂住」时不要人工二分，先用 `--blame-hang --blame-hang-timeout`**：
一次运行就能拿到挂住的方法名 + 进程转储，比按 namespace 分批试要省一个数量级的时间。
