"""Runs an abstractive candidate (identity, haiku_digest, summarizer_control) and the QA reader
through pi, and prints one JSON line per run with the reader's replies.

  python3 pi_runner.py <candidate> <runs> <payload name>...

Each pi call is isolated (no tools, session, context files, extensions, skills, templates or MCP)
and has the proxy variables cleared, so it never passes through ai-proxy.
"""
import json
import os
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

READER_REPLIES = 3
PARALLEL = 4
FAILED_CALL = re.compile(r"^(OpenAI API error|Connection error|Error:)", re.IGNORECASE)

SYSTEMS = {
    "haiku_digest": (
        "You compress tool output for a coding agent that will act on it. Rewrite the output as short as "
        "possible while keeping every fact the agent may need: names, identifiers, paths, numbers, versions, "
        "error codes and messages, exactly as written. Reply with the compressed output only."),
    "summarizer_control": "Summarize the text in at most two short sentences. Reply with the summary only.",
}
MODEL_ROLE = {"haiku_digest": "digest", "summarizer_control": "summarizer"}
READER_SYSTEM = (
    "Answer questions using only the text you are given. Reply with a JSON array of strings, one answer "
    "per question, in order. Copy each answer exactly as it appears in the text. When the text does not "
    "contain the answer, use \"UNKNOWN\".")


def qa_prompt(text: str, questions: list[dict]) -> str:
    """The reader's prompt: the text, then the numbered questions, never the expected answers."""
    asked = "\n".join(f"{i}. {q['q']}" for i, q in enumerate(questions, 1))
    return f"<text>\n{text}\n</text>\n\nQuestions:\n{asked}"


def parse_answers(raw: str, count: int) -> list[str]:
    """One answer per question from a JSON array reply; anything else answers nothing."""
    stripped = re.sub(r"^```(?:json)?\s*|\s*```$", "", raw.strip())
    try:
        answers = json.loads(stripped)
    except json.JSONDecodeError:
        return [""] * count
    if not isinstance(answers, list) or len(answers) != count:
        return [""] * count
    return [str(a) for a in answers]


def pi_call(model: str, system: str, prompt: str) -> tuple[str, float]:
    """One isolated pi call; returns the reply and the process wall time, or raises when the call failed."""
    env = {k: v for k, v in os.environ.items()
           if k.upper() not in ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS")}
    env["NO_PROXY"] = "*"
    started = time.perf_counter()
    done = subprocess.run(
        ["pi", "-p", "--model", model, "--no-tools", "--no-session", "--no-context-files", "--no-extensions",
         "--no-skills", "--no-prompt-templates", "--no-mcp", "--system-prompt", system, "--", prompt],
        env=env, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=600)
    elapsed = time.perf_counter() - started
    reply = done.stdout.strip()
    if done.returncode != 0 or not reply or FAILED_CALL.match(reply):
        raise RuntimeError(f"pi call failed (exit {done.returncode}): {(reply or done.stderr)[:300]}")
    return reply, elapsed


def main() -> None:
    from preregistration import load, payload_text

    candidate, runs, names = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
    prereg = load()
    models = prereg["amendments"][-1]["models"]
    reader = f"{models['qa_reader']}:{models['qa_effort']}"
    reducer = None if candidate == "identity" else f"{models[MODEL_ROLE[candidate]]}:off"

    def one_run(job: tuple[str, int]) -> dict:
        name, run = job
        text = payload_text(name)
        questions = prereg["payloads"][name]["questions"]
        if reducer is None:
            output, latency = text, 0.0
        else:
            output, latency = pi_call(reducer, SYSTEMS[candidate], text)
        raws = [pi_call(reader, READER_SYSTEM, qa_prompt(output, questions))[0] for _ in range(READER_REPLIES)]
        parsed = [parse_answers(raw, len(questions)) for raw in raws]
        replies = [[p[i] for p in parsed] for i in range(len(questions))]
        return {"payload": name, "run": run, "output": output, "latency_s": latency,
                "replies": replies, "raw_replies": raws}

    jobs = [(name, run) for name in names for run in range(runs)]
    with ThreadPoolExecutor(PARALLEL) as pool:
        for record in pool.map(one_run, jobs):
            print(json.dumps(record), flush=True)


if __name__ == "__main__":
    main()
