Read Task.md. It is the plan: verified, checked, and final. Honor this contract and execute it top to bottom, checkpoint by checkpoint, as written. Re-verifying the plan is out of scope.

Each checkpoint is a local commit. A checkpoint is a save point: do its boxes, commit, move on. Validating is that commit; the "Checkpoint erfüllt" line is the only bar.

You are authorized to make destructive public API changes.

The build is red the whole way through, and that is correct. Red is the expected state between checkpoints and changes nothing about the next step. Where Task.md says to run build or tests, run them and record the result; they are a log, not a gate. Green is not a goal of this plan. Remaining errors get reported at the end, as Checkpoint 7 says.
