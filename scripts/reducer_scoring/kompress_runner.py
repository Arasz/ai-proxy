"""Runs kompress int8 over each payload and prints one JSON line per timed run.

Needs the kompress venv (onnxruntime, tokenizers, numpy) and the model directory:
  <venv>/bin/python kompress_runner.py <model dir> <runs> <payload name>...
"""
import json
import sys
import time


def chunk_spans(length: int, size: int) -> list[tuple[int, int]]:
    """Consecutive [start, end) spans covering every token, none longer than size."""
    return [(start, min(start + size, length)) for start in range(0, length, size)]


def main() -> None:
    import os

    import numpy as np
    import onnxruntime as ort
    from tokenizers import Tokenizer

    from preregistration import load, payload_text

    model_dir, runs, names = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
    settings = load()["kompress"]
    tok = Tokenizer.from_file(os.path.join(model_dir, "tokenizer.json"))
    session = ort.InferenceSession(os.path.join(model_dir, settings["onnx"]),
                                   providers=["CPUExecutionProvider"])

    def reduce(text: str) -> str:
        enc = tok.encode(text)
        ids = np.array([enc.ids], dtype=np.int64)
        mask = np.array([enc.attention_mask], dtype=np.int64)
        scores = np.zeros(ids.shape[1], dtype=np.float32)
        for start, end in chunk_spans(ids.shape[1], settings["chunk_tokens"]):
            scores[start:end] = session.run(["final_scores"], {
                "input_ids": ids[:, start:end], "attention_mask": mask[:, start:end]})[0][0]
        keep = [i for i, s in zip(enc.ids, scores) if s >= settings["threshold"]]
        return tok.decode(keep, skip_special_tokens=True)

    for name in names:
        text = payload_text(name)
        reduce(text)
        for run in range(runs):
            started = time.perf_counter()
            output = reduce(text)
            latency = time.perf_counter() - started
            print(json.dumps({"payload": name, "run": run, "output": output, "latency_s": latency}), flush=True)


if __name__ == "__main__":
    main()
