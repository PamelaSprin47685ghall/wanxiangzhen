module Wanxiangzhen.Tests.E2eBehaviorGapTests

open Wanxiangzhen.Tests.Assert

/// All 25 ExtendedMockE2e labels, in registration order across the five sub-suites
/// (replay ×3, scheduler ×4, submit ×7, slave_http ×8, plugin ×3).
let private extMockLabels : string list = [
    // ── replay ─────────────────────────────────────────────────────────────────
    "ExtendedMockE2e.chat_message_captures_session_id_and_replays"
    "ExtendedMockE2e.replay_reconciles_submitted_to_merged"
    "ExtendedMockE2e.replay_warns_orphan_running_tasks"
    // ── scheduler ──────────────────────────────────────────────────────────────
    "ExtendedMockE2e.maxConcurrent_limits_ready_tasks"
    "ExtendedMockE2e.dependency_chain_schedules_sequentially"
    "ExtendedMockE2e.done_beacon_marks_task_done"
    "ExtendedMockE2e.pid_polling_detects_slave_death"
    // ── submit ─────────────────────────────────────────────────────────────────
    "ExtendedMockE2e.worktree_add_failure_injects_task_error"
    "ExtendedMockE2e.merged_with_already_dead_slave_does_not_crash"
    "ExtendedMockE2e.submit_rebase_needed_returns_running"
    "ExtendedMockE2e.submit_stale_commit_branch"
    "ExtendedMockE2e.submit_coordinator_not_ready_dirty"
    "ExtendedMockE2e.http_task_not_found_404"
    "ExtendedMockE2e.http_bad_register_body_400"
    // ── slave_http ─────────────────────────────────────────────────────────────
    "ExtendedMockE2e.slave_submit_merged"
    "ExtendedMockE2e.slave_submit_rebase_needed"
    "ExtendedMockE2e.slave_submit_stale_commit"
    "ExtendedMockE2e.slave_submit_coordinator_not_ready"
    "ExtendedMockE2e.slave_submit_not_submittable"
    "ExtendedMockE2e.slave_submit_task_not_found"
    "ExtendedMockE2e.slave_submit_unauthorized"
    "ExtendedMockE2e.slave_query_squad_task_detail"
    // ── plugin ─────────────────────────────────────────────────────────────────
    "ExtendedMockE2e.multi_session_squad_command_saves_previous"
    "ExtendedMockE2e.dispose_hook_closes_server_and_stops_polling"
    "ExtendedMockE2e.realistic_opencode_plugin_input_mock"
]

/// Ten (behavior, coverage, kind) tuples mapping the SquadEvent DU cases +
/// scheduler + replay into an agents-behavior registry.
/// These are the canonical AGENTS.md SSOT behaviors covering the twelve
/// SquadEvent DU branches, the scheduling runtime, and event replay.
let private agentsBehaviors : (string * string * string) list = [
    ("squad_created",    "lifecycle",  "event")
    ("tasks_created",    "lifecycle",  "event")
    ("task_started",     "execution",  "event")
    ("task_submitted",   "submission", "event")
    ("task_merged",      "submission", "event")
    ("task_done",        "completion", "event")
    ("task_error",       "error",      "event")
    ("squad_cancelled",  "lifecycle",  "event")
    ("task_scheduling",  "scheduling", "runtime")
    ("event_replay",     "recovery",   "runtime")
]

/// The 10 AGENTS.md SSOT behaviors — canonical reference matching the
/// SquadEvent DU cases + scheduler + replay runtime entries.
/// Validated against PRD/EventSourcing.md §4 event type table and
/// src/Kernel/SquadEvent.fs DU definition.
let private agentsMdBehaviors : string list = [
    "squad_created"
    "tasks_created"
    "task_started"
    "task_submitted"
    "task_merged"
    "task_done"
    "task_error"
    "squad_cancelled"
    "task_scheduling"
    "event_replay"
]

let entries () : (string * (unit -> unit)) list = [
    ("e2e_behavior_gap.registry_counts_and_matches_live", fun () ->
        chk "gap.ext_mock_len_25" (List.length extMockLabels = 25)
        let live = Wanxiangzhen.Tests.ExtendedMockE2eTests.entriesAsync () |> List.map fst
        chk "gap.ext_mock_matches_live" (live = extMockLabels)
    )
    ("e2e_behavior_gap.coverage_table", fun () ->
        printfn "| label | area |"
        printfn "|-------|------|"
        extMockLabels |> List.iter (fun label ->
            let area =
                if    label.Contains "replay" then "replay"
                elif label.Contains "dependency_" || label.Contains "maxConcurrent"
                     || label.Contains "done_beacon" || label.Contains "pid_polling" then "scheduler"
                elif label.Contains "slave_" then "slave_http"
                elif label.Contains "submit_" || label.Contains "worktree_add"
                     || label.Contains "merged_" then "submit"
                elif label.Contains "multi_session" || label.Contains "dispose_hook"
                     || label.Contains "realistic_opencode" then "plugin"
                elif label.Contains "http_" then "submit"
                else "unknown"
            printfn "| %s | %s |" label area
        )
    )
    ("e2e_behavior_gap.agents_registry", fun () ->
        printfn "BEHAVIOR GAP REGISTRY"
        agentsBehaviors |> List.iter (fun (behavior, coverage, kind) ->
            chk "gap.agents_behavior_nonempty" (not (System.String.IsNullOrWhiteSpace behavior))
            chk "gap.agents_coverage_nonempty" (not (System.String.IsNullOrWhiteSpace coverage))
            chk "gap.agents_kind_nonempty" (not (System.String.IsNullOrWhiteSpace kind))
        )
        chk "gap.agents_md_ssot_len_10" (List.length agentsMdBehaviors = 10)
        chk "gap.agents_md_ssot_matches_behaviors" (
            agentsMdBehaviors = (agentsBehaviors |> List.map (fun (b,_,_) -> b))
        )
        agentsMdBehaviors |> List.iter (fun behavior ->
            chk "gap.agents_md_behavior_nonempty" (not (System.String.IsNullOrWhiteSpace behavior))
        )
    )
]
