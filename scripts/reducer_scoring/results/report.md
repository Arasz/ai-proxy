controls clear the run

{"date": "2026-10-24", "machine": "Apple M4, 24 GB, macOS 27.0.1", "commit": "0d7d19b", "rtk_proxy": "rtk-proxy 1.0.1", "pi": "1.4.0"}

## Per kind

| candidate | kind | score | compression | latency s | retention | answers | score @0.5 s | score @2 s |
|---|---|---|---|---|---|---|---|---|
| haiku_digest (stand-in) | code | 0.0 | 0.7308 | 18.8149 | 0.9167 | 11/12 | 0.0 | 0.0 |
| haiku_digest (stand-in) | prose | 0.067 | 0.3123 | 25.6695 | 1.0 | 14/14 | 0.036 | 0.1174 |
| haiku_digest (stand-in) | log | 0.1202 | 0.6701 | 14.1491 | 1.0 | 13/13 | 0.065 | 0.2091 |
| haiku_digest (stand-in) | listing | 0.0 | 0.647 | 4.3748 | 0.8333 | 5/6 | 0.0 | 0.0 |
| haiku_digest (stand-in) | json | 0.0922 | 0.3189 | 17.5466 | 1.0 | 19/19 | 0.051 | 0.1549 |
| haiku_digest (stand-in) | build | 0.0 | 0.985 | 6.9746 | 0.9 | 9/10 | 0.0 | 0.0 |
| haiku_digest (stand-in) | test | 0.0 | 0.5599 | 13.1300 | 0.8 | 8/10 | 0.0 | 0.0 |
| identity | code | 0.0 | 0.0 | 0.0000 | 1.0 | 12/12 | 0.0 | 0.0 |
| identity | prose | 0.0 | 0.0 | 0.0000 | 1.0 | 14/14 | 0.0 | 0.0 |
| identity | log | 0.0 | 0.0 | 0.0000 | 1.0 | 13/13 | 0.0 | 0.0 |
| identity | listing | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | json | 0.0 | 0.0 | 0.0000 | 1.0 | 19/19 | 0.0 | 0.0 |
| identity | build | 0.0 | 0.0 | 0.0000 | 1.0 | 10/10 | 0.0 | 0.0 |
| identity | test | 0.0 | 0.0 | 0.0000 | 1.0 | 10/10 | 0.0 | 0.0 |
| kompress_int8 | code | 0.0876 | 0.0536 | 3.1788 | 1.0 | 12/12 | 0.0769 | 0.0941 |
| kompress_int8 | prose | 0.1711 | 0.1227 | 2.5391 | 1.0 | 14/14 | 0.1405 | 0.1919 |
| kompress_int8 | log | 0.0019 | 0.001 | 1.3578 | 1.0 | 13/13 | 0.0019 | 0.0019 |
| kompress_int8 | listing | n/a | | | | | | |
| kompress_int8 | json | 0.0899 | 0.0509 | 1.6045 | 1.0 | 5/5 | 0.0838 | 0.0933 |
| kompress_int8 | build | 0.0 | 0.004 | 69.2786 | 0.8333 | 5/6 | 0.0 | 0.0 |
| kompress_int8 | test | 0.0 | 0.0466 | 1.0031 | 0.7 | 7/10 | 0.0 | 0.0 |
| rtk_ccr | code | 0.0 | 0.7317 | 0.0030 | 0.75 | 9/12 | 0.0 | 0.0 |
| rtk_ccr | prose | 0.0 | 0.8043 | 0.0013 | 0.5 | 7/14 | 0.0 | 0.0 |
| rtk_ccr | log | 0.0 | 0.3198 | 0.0009 | 0.6154 | 8/13 | 0.0 | 0.0 |
| rtk_ccr | listing | n/a | | | | | | |
| rtk_ccr | json | 0.0 | 0.8928 | 0.0005 | 0.0 | 0/19 | 0.0 | 0.0 |
| rtk_ccr | build | 0.0 | 0.9753 | 0.0050 | 0.3333 | 2/6 | 0.0 | 0.0 |
| rtk_ccr | test | 0.0 | 0.5839 | 0.0007 | 0.6 | 6/10 | 0.0 | 0.0 |
| shipped_rules | code | 0.3048 | 0.1799 | 0.0022 | 1.0 | 12/12 | 0.3047 | 0.3048 |
| shipped_rules | prose | n/a | | | | | | |
| shipped_rules | log | 0.0 | 0.6966 | 0.0006 | 0.6923 | 9/13 | 0.0 | 0.0 |
| shipped_rules | listing | n/a | | | | | | |
| shipped_rules | json | 0.4642 | 0.3023 | 0.0002 | 1.0 | 14/14 | 0.4642 | 0.4642 |
| shipped_rules | build | 0.4147 | 0.2616 | 0.0003 | 1.0 | 4/4 | 0.4147 | 0.4148 |
| shipped_rules | test | 0.4022 | 0.2518 | 0.0009 | 1.0 | 5/5 | 0.4021 | 0.4022 |
| summarizer_control | code | 0.0 | 0.9681 | 5.0226 | 0.1667 | 2/12 | 0.0 | 0.0 |
| summarizer_control | prose | 0.0 | 0.9406 | 5.5997 | 0.1429 | 2/14 | 0.0 | 0.0 |
| summarizer_control | log | 0.0 | 0.9404 | 4.5205 | 0.1538 | 2/13 | 0.0 | 0.0 |
| summarizer_control | listing | 0.0 | 0.6939 | 4.2574 | 0.1667 | 1/6 | 0.0 | 0.0 |
| summarizer_control | json | 0.0 | 0.9157 | 5.3232 | 0.1053 | 2/19 | 0.0 | 0.0 |
| summarizer_control | build | 0.0 | 0.9924 | 4.6448 | 0.5 | 5/10 | 0.0 | 0.0 |
| summarizer_control | test | 0.0 | 0.9088 | 4.7100 | 0.5 | 5/10 | 0.0 | 0.0 |

## Per payload

| candidate | payload | score | compression | latency s | retention | answers | score @0.5 s | score @2 s |
|---|---|---|---|---|---|---|---|---|
| haiku_digest (stand-in) | code_file | 0.0 | 0.7198 | 12.8938 | 0.8333 | 5/6 | 0.0 | 0.0 |
| haiku_digest (stand-in) | incident_report | 0.1468 | 0.2684 | 8.8933 | 1.0 | 8/8 | 0.0888 | 0.218 |
| haiku_digest (stand-in) | agent_log | 0.1318 | 0.481 | 12.0943 | 1.0 | 8/8 | 0.0733 | 0.2192 |
| haiku_digest (stand-in) | dir_listing | 0.0 | 0.647 | 4.3748 | 0.8333 | 5/6 | 0.0 | 0.0 |
| haiku_digest (stand-in) | json_payload | 0.1734 | 0.3023 | 7.2230 | 1.0 | 14/14 | 0.1066 | 0.2525 |
| haiku_digest (stand-in) | dotnet_build_failure | 0.2141 | 0.5854 | 6.6323 | 1.0 | 4/4 | 0.1252 | 0.332 |
| haiku_digest (stand-in) | dotnet_test_failure | 0.1643 | 0.3833 | 8.5641 | 1.0 | 5/5 | 0.0964 | 0.2535 |
| haiku_digest (stand-in) | code_file_large | 0.0738 | 0.7373 | 24.7361 | 1.0 | 6/6 | 0.0386 | 0.1358 |
| haiku_digest (stand-in) | prose_large | 0.0429 | 0.3179 | 42.4456 | 1.0 | 6/6 | 0.0225 | 0.0788 |
| haiku_digest (stand-in) | rtk_proxy_log | 0.1079 | 0.7537 | 16.2039 | 1.0 | 5/5 | 0.0576 | 0.1918 |
| haiku_digest (stand-in) | github_pr_list | 0.0625 | 0.3216 | 27.8702 | 1.0 | 5/5 | 0.0334 | 0.1108 |
| haiku_digest (stand-in) | dotnet_build_failure_verbose | 0.0 | 0.9909 | 7.3170 | 0.8333 | 5/6 | 0.0 | 0.0 |
| haiku_digest (stand-in) | dotnet_test_failures | 0.0 | 0.5969 | 17.6959 | 0.6 | 3/5 | 0.0 | 0.0 |
| identity | code_file | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | incident_report | 0.0 | 0.0 | 0.0000 | 1.0 | 8/8 | 0.0 | 0.0 |
| identity | agent_log | 0.0 | 0.0 | 0.0000 | 1.0 | 8/8 | 0.0 | 0.0 |
| identity | dir_listing | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | json_payload | 0.0 | 0.0 | 0.0000 | 1.0 | 14/14 | 0.0 | 0.0 |
| identity | dotnet_build_failure | 0.0 | 0.0 | 0.0000 | 1.0 | 4/4 | 0.0 | 0.0 |
| identity | dotnet_test_failure | 0.0 | 0.0 | 0.0000 | 1.0 | 5/5 | 0.0 | 0.0 |
| identity | code_file_large | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | prose_large | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | rtk_proxy_log | 0.0 | 0.0 | 0.0000 | 1.0 | 5/5 | 0.0 | 0.0 |
| identity | github_pr_list | 0.0 | 0.0 | 0.0000 | 1.0 | 5/5 | 0.0 | 0.0 |
| identity | dotnet_build_failure_verbose | 0.0 | 0.0 | 0.0000 | 1.0 | 6/6 | 0.0 | 0.0 |
| identity | dotnet_test_failures | 0.0 | 0.0 | 0.0000 | 1.0 | 5/5 | 0.0 | 0.0 |
| kompress_int8 | code_file | 0.0994 | 0.0582 | 1.9380 | 1.0 | 6/6 | 0.0907 | 0.1044 |
| kompress_int8 | incident_report | 0.1752 | 0.0982 | 0.2277 | 1.0 | 8/8 | 0.1718 | 0.177 |
| kompress_int8 | agent_log | 0.0039 | 0.002 | 0.6411 | 1.0 | 8/8 | 0.0039 | 0.0039 |
| kompress_int8 | dir_listing | n/a | | | | | | |
| kompress_int8 | json_payload | n/a | | | | | | |
| kompress_int8 | dotnet_build_failure | n/a | | | | | | |
| kompress_int8 | dotnet_test_failure | 0.0 | 0.0196 | 0.2518 | 0.6 | 3/5 | 0.0 | 0.0 |
| kompress_int8 | code_file_large | 0.0798 | 0.0509 | 4.4196 | 1.0 | 6/6 | 0.0678 | 0.0875 |
| kompress_int8 | prose_large | 0.1449 | 0.1258 | 4.8505 | 1.0 | 6/6 | 0.1072 | 0.1758 |
| kompress_int8 | rtk_proxy_log | 0.001 | 0.0005 | 2.0745 | 1.0 | 5/5 | 0.001 | 0.001 |
| kompress_int8 | github_pr_list | 0.0899 | 0.0509 | 1.6045 | 1.0 | 5/5 | 0.0838 | 0.0933 |
| kompress_int8 | dotnet_build_failure_verbose | 0.0 | 0.004 | 69.2786 | 0.8333 | 5/6 | 0.0 | 0.0 |
| kompress_int8 | dotnet_test_failures | 0.0 | 0.0522 | 1.7544 | 0.8 | 4/5 | 0.0 | 0.0 |
| rtk_ccr | code_file | 0.747 | 0.5974 | 0.0033 | 1.0 | 6/6 | 0.7461 | 0.7475 |
| rtk_ccr | incident_report | 0.0 | 0.5056 | 0.0014 | 0.5 | 4/8 | 0.0 | 0.0 |
| rtk_ccr | agent_log | 0.0 | 0.7233 | 0.0009 | 0.375 | 3/8 | 0.0 | 0.0 |
| rtk_ccr | dir_listing | n/a | | | | | | |
| rtk_ccr | json_payload | 0.0 | 0.5807 | 0.0005 | 0.0 | 0/14 | 0.0 | 0.0 |
| rtk_ccr | dotnet_build_failure | n/a | | | | | | |
| rtk_ccr | dotnet_test_failure | 0.0 | 0.4037 | 0.0005 | 0.6 | 3/5 | 0.0 | 0.0 |
| rtk_ccr | code_file_large | 0.0 | 0.8104 | 0.0026 | 0.5 | 3/6 | 0.0 | 0.0 |
| rtk_ccr | prose_large | 0.0 | 0.8424 | 0.0013 | 0.5 | 3/6 | 0.0 | 0.0 |
| rtk_ccr | rtk_proxy_log | 0.2478 | 0.1414 | 0.0009 | 1.0 | 5/5 | 0.2477 | 0.2478 |
| rtk_ccr | github_pr_list | 0.0 | 0.9441 | 0.0004 | 0.0 | 0/5 | 0.0 | 0.0 |
| rtk_ccr | dotnet_build_failure_verbose | 0.0 | 0.9753 | 0.0050 | 0.3333 | 2/6 | 0.0 | 0.0 |
| rtk_ccr | dotnet_test_failures | 0.0 | 0.6218 | 0.0008 | 0.6 | 3/5 | 0.0 | 0.0 |
| shipped_rules | code_file | 0.2587 | 0.1486 | 0.0020 | 1.0 | 6/6 | 0.2587 | 0.2587 |
| shipped_rules | incident_report | n/a | | | | | | |
| shipped_rules | agent_log | 0.4002 | 0.2502 | 0.0006 | 1.0 | 8/8 | 0.4002 | 0.4002 |
| shipped_rules | dir_listing | n/a | | | | | | |
| shipped_rules | json_payload | 0.4642 | 0.3023 | 0.0002 | 1.0 | 14/14 | 0.4642 | 0.4642 |
| shipped_rules | dotnet_build_failure | 0.4147 | 0.2616 | 0.0003 | 1.0 | 4/4 | 0.4147 | 0.4148 |
| shipped_rules | dotnet_test_failure | n/a | | | | | | |
| shipped_rules | code_file_large | 0.3307 | 0.1982 | 0.0024 | 1.0 | 6/6 | 0.3305 | 0.3307 |
| shipped_rules | prose_large | n/a | | | | | | |
| shipped_rules | rtk_proxy_log | 0.0 | 0.8941 | 0.0007 | 0.2 | 1/5 | 0.0 | 0.0 |
| shipped_rules | github_pr_list | n/a | | | | | | |
| shipped_rules | dotnet_build_failure_verbose | n/a | | | | | | |
| shipped_rules | dotnet_test_failures | 0.4022 | 0.2518 | 0.0009 | 1.0 | 5/5 | 0.4021 | 0.4022 |
| summarizer_control | code_file | 0.0 | 0.9623 | 5.4108 | 0.1667 | 1/6 | 0.0 | 0.0 |
| summarizer_control | incident_report | 0.0 | 0.7804 | 5.3266 | 0.25 | 2/8 | 0.0 | 0.0 |
| summarizer_control | agent_log | 0.0 | 0.9087 | 4.4370 | 0.0 | 0/8 | 0.0 | 0.0 |
| summarizer_control | dir_listing | 0.0 | 0.6939 | 4.2574 | 0.1667 | 1/6 | 0.0 | 0.0 |
| summarizer_control | json_payload | 0.0 | 0.7943 | 4.2327 | 0.1429 | 2/14 | 0.0 | 0.0 |
| summarizer_control | dotnet_build_failure | 0.0 | 0.7816 | 4.4617 | 0.5 | 2/4 | 0.0 | 0.0 |
| summarizer_control | dotnet_test_failure | 0.2761 | 0.7411 | 4.8938 | 1.0 | 5/5 | 0.1648 | 0.417 |
| summarizer_control | code_file_large | 0.0 | 0.9714 | 4.6345 | 0.1667 | 1/6 | 0.0 | 0.0 |
| summarizer_control | prose_large | 0.0 | 0.961 | 5.8729 | 0.0 | 0/6 | 0.0 | 0.0 |
| summarizer_control | rtk_proxy_log | 0.0 | 0.9544 | 4.6040 | 0.4 | 2/5 | 0.0 | 0.0 |
| summarizer_control | github_pr_list | 0.0 | 0.9357 | 6.4137 | 0.0 | 0/5 | 0.0 | 0.0 |
| summarizer_control | dotnet_build_failure_verbose | 0.0 | 0.9956 | 4.8279 | 0.5 | 3/6 | 0.0 | 0.0 |
| summarizer_control | dotnet_test_failures | 0.0 | 0.944 | 4.5262 | 0.0 | 0/5 | 0.0 | 0.0 |
