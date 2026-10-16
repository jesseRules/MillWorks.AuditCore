# October 11 historical post-rebuild record


Jesse confirmed the API was rebuilt and running. The new process started at 10:57:10 local time on October 11. Its output dependency manifest references the updated package versions; the AuditCore and BackgroundJobs persistence DLLs match their restored published packages. The agent did not rebuild or restart it.

A 60-second read-only SQL observation at approximately 11:03 local time recorded 12 samples, 0 SQL probe failures, a maximum SQL probe time of 177.65 ms, and a maximum node heartbeat age of 2930 ms. All samples showed the API node online and unfenced; observed generations were [1], with a minimum leadership lease margin of 2079 ms. This verifies the observed steady-state window; it does not reproduce the original host stall. Raw evidence: `/tmp/mw-sql-post-rebuild-20261011`. Swap usage was approximately 7.4 GiB at the start of this check, down from 22.3 GiB during the earlier investigation.
