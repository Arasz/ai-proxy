"""The pure parts of scoring rtk's CCR preview with retrieval allowed: marker ids, the RETRIEVE reply, the prompts."""
import re

from pi_runner import READER_SYSTEM

MARKER = re.compile(r'rtk_retrieve\("([0-9a-f]+)"\)')
REQUEST = re.compile(r"RETRIEVE ([0-9a-f]+)")
RETRIEVAL_SYSTEM = (
    READER_SYSTEM + " When the text ends with an rtk_retrieve marker and does not contain an answer, you may "
    "reply with exactly `RETRIEVE <id>`, using the id from the marker, instead of the JSON array. The "
    "original text is then supplied and you answer from it.")


def marker_ids(text: str) -> list[str]:
    """The ids of the rtk_retrieve markers in the text."""
    return MARKER.findall(text)


def parse_retrieve(reply: str, text: str) -> str | None:
    """The id a reply asks to retrieve; None unless the reply is exactly `RETRIEVE <id>` for an id in the text."""
    found = REQUEST.fullmatch(reply.strip())
    if found and found[1] in marker_ids(text):
        return found[1]
    return None


def retrieval_prompt(preview: str, original: str, questions: list[dict]) -> str:
    """The follow-up prompt: the preview, the retrieved original, then the numbered questions."""
    asked = "\n".join(f"{i}. {q['q']}" for i, q in enumerate(questions, 1))
    return f"<text>\n{preview}\n</text>\n\n<retrieved>\n{original}\n</retrieved>\n\nQuestions:\n{asked}"


def retrieval_counts(records: list[dict]) -> tuple[int, int]:
    """Reader calls that retrieved, and all reader calls, over records that each hold a retrievals flag per call."""
    flags = [flag for record in records for flag in record["retrievals"]]
    return sum(flags), len(flags)
