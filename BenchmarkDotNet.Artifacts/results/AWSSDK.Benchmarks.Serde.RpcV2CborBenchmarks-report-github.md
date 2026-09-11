```

BenchmarkDotNet v0.15.2, Linux Amazon Linux 2023.12.20260909
Neoverse-N1, 16 physical cores
.NET SDK 10.0.111
  [Host] : .NET 8.0.30 (8.0.3026.36720), Arm64 RyuJIT AdvSIMD

Toolchain=InProcessEmitToolchain  MaxIterationCount=20  MinIterationCount=5  
RunStrategy=Throughput  WarmupCount=5  

```
| Method                                | Mean         | Error       | StdDev    | Max          | P50          | P90          | P95          | Gen0    | Gen1    | Gen2    | Allocated |
|-------------------------------------- |-------------:|------------:|----------:|-------------:|-------------:|-------------:|-------------:|--------:|--------:|--------:|----------:|
| rpcv2Cbor_GetItemOutput_Baseline      |     786.3 ns |    12.85 ns |   3.34 ns |     790.0 ns |     785.0 ns |     789.8 ns |     789.9 ns |  0.0744 |       - |       - |   1.23 KB |
| rpcv2Cbor_GetItemOutput_S             |   3,596.1 ns |    32.89 ns |   8.54 ns |   3,607.7 ns |   3,591.3 ns |   3,605.7 ns |   3,606.7 ns |  0.1755 |       - |       - |    2.9 KB |
| rpcv2Cbor_GetItemOutput_M             |  11,065.8 ns |    59.36 ns |   9.19 ns |  11,073.4 ns |  11,068.7 ns |  11,072.6 ns |  11,073.0 ns |  0.4730 |       - |       - |   7.91 KB |
| rpcv2Cbor_GetItemOutput_L             |  25,330.5 ns |   290.90 ns |  45.02 ns |  25,358.7 ns |  25,349.9 ns |  25,357.1 ns |  25,357.9 ns |  1.0376 |  0.0305 |       - |  17.19 KB |
| rpcv2Cbor_GetItemOutputBinary_S       |   2,125.5 ns |    42.95 ns |  15.32 ns |   2,137.4 ns |   2,132.4 ns |   2,135.7 ns |   2,136.6 ns |  0.1335 |       - |       - |   2.19 KB |
| rpcv2Cbor_GetItemOutputBinary_M       |   7,059.5 ns |    44.31 ns |   6.86 ns |   7,063.8 ns |   7,062.4 ns |   7,063.6 ns |   7,063.7 ns |  2.0752 |  0.2289 |       - |  34.05 KB |
| rpcv2Cbor_GetItemOutputBinary_L       |  81,432.5 ns | 1,377.10 ns | 491.09 ns |  82,125.9 ns |  81,328.0 ns |  82,017.1 ns |  82,071.5 ns | 43.0908 | 42.9688 | 42.9688 | 258.25 KB |
| rpcv2Cbor_PutItemRequest_Baseline     |   1,548.2 ns |    24.09 ns |   3.73 ns |   1,550.9 ns |   1,549.6 ns |   1,550.6 ns |   1,550.8 ns |  0.0782 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_BinaryData_S |   1,854.6 ns |    17.41 ns |   2.69 ns |   1,856.8 ns |   1,855.4 ns |   1,856.5 ns |   1,856.6 ns |  0.0954 |       - |       - |   1.56 KB |
| rpcv2Cbor_PutItemRequest_BinaryData_M |   7,180.1 ns |    57.17 ns |  14.85 ns |   7,197.6 ns |   7,182.7 ns |   7,193.4 ns |   7,195.5 ns |  2.0294 |  0.0076 |       - |  33.31 KB |
| rpcv2Cbor_PutItemRequest_BinaryData_L |  92,826.6 ns | 1,562.22 ns | 557.10 ns |  93,842.7 ns |  92,672.4 ns |  93,436.9 ns |  93,639.8 ns | 45.8984 | 45.7764 | 45.7764 | 257.57 KB |
| rpcv2Cbor_PutItemRequest_MixedItem_S  |   2,487.9 ns |    27.69 ns |   4.29 ns |   2,492.2 ns |   2,488.7 ns |   2,491.2 ns |   2,491.7 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_MixedItem_M  |   7,167.7 ns |    43.81 ns |   6.78 ns |   7,172.2 ns |   7,170.4 ns |   7,172.1 ns |   7,172.1 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_MixedItem_L  |  17,747.1 ns |   190.98 ns |  29.55 ns |  17,777.8 ns |  17,749.9 ns |  17,773.3 ns |  17,775.6 ns |  0.0610 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_Nested_M     |   4,118.1 ns |    17.44 ns |   2.70 ns |   4,120.6 ns |   4,118.6 ns |   4,120.4 ns |   4,120.5 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_Nested_L     |   9,235.8 ns |   112.37 ns |  17.39 ns |   9,248.8 ns |   9,241.3 ns |   9,248.8 ns |   9,248.8 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_ShallowMap_S |   2,736.2 ns |    10.36 ns |   2.69 ns |   2,738.7 ns |   2,736.9 ns |   2,738.5 ns |   2,738.6 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_ShallowMap_M |  12,235.2 ns |   136.87 ns |  35.54 ns |  12,291.2 ns |  12,234.0 ns |  12,271.2 ns |  12,281.2 ns |  0.0763 |       - |       - |   1.29 KB |
| rpcv2Cbor_PutItemRequest_ShallowMap_L | 108,513.6 ns |   539.40 ns | 140.08 ns | 108,690.0 ns | 108,477.5 ns | 108,657.7 ns | 108,673.9 ns |       - |       - |       - |   1.29 KB |
