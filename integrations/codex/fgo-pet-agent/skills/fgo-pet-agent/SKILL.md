---
name: fgo-pet-agent
description: Report confirmed Codex task and goal outcomes to FGO Pet.
---

# FGO Pet Agent bridge

Use this integration only after the user has explicitly confirmed a task
delivery or goal delivery in Codex. A task stopping output, asking a question,
or reaching a milestone is not completion. The adapter must be paired and
approved in FGO Pet first; while approval is pending, report that pairing is
required instead of retrying indefinitely.

- Keep prompts, transcripts, tool arguments, environment values, credentials,
  and local paths inside Codex. Never put a Relay credential in a prompt,
  command argument, or environment variable.
- Use `report_task_completed` only after user acceptance of the delivered task.
- Use `report_goal_completed` only after user acceptance and include only the
  covered FGO Pet task keys.
- If the bridge is unavailable, leave the task state unchanged and tell the
  user that confirmation can be reported later.
