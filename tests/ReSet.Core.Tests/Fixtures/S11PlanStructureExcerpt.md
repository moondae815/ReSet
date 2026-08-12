```json
{
  "Steps": [
    {
      "Code": "S11",
      "Name": "취소영향 요약 보정",
      "LegacyProcedures": ["UP_UTIL_SETTLE_SUMMARY_ETC"],
      "TargetTables": ["TSettleByTX", "TPartialCancelByTX", "TSettleByIN", "TSettleByOUT"],
      "ErrorCodes": ["-1", "-2", "-3"],
      "Chunkable": false
    }
  ]
}
```
