"""🔒 MANUAL — requirement-by-requirement evidence and neutral explanations."""

from app.matching.requirement_models import JobRequirement, RequirementMatch
from app.models import CandidateProfile, Chunk


def compare_requirements(
    requirements: list[JobRequirement],
    chunks: list[Chunk],
    profile: CandidateProfile | None = None,
) -> list[RequirementMatch]:
    """Compare each requirement with one candidate's CV chunks and extracted profile.

    Rules:
        - "found": at least one verbatim quote supports it (quotes must be exact substrings
          of a chunk; never edited).
        - "not_found": no supporting evidence. Note must be neutral, e.g.
          "Not found in available CV/profile evidence" — absence of evidence is not proof.
        - "needs_verification": weak/indirect evidence (e.g. a skill only listed, or years that
          can't be computed from dates).

    Args:
        requirements: The job's requirements.
        chunks: The candidate's chunks (same workspace).
        profile: The candidate's extracted profile, if available.

    Returns:
        One RequirementMatch per requirement, in the same order.
    """
    raise NotImplementedError("MANUAL: requirement-by-requirement comparison")


def summarize_match(matches: list[RequirementMatch], language: str = "en") -> str:
    """One or two neutral sentences summarizing found / missing / to-verify requirements.

    Never mention hiring probability, performance, personality or protected characteristics.
    """
    raise NotImplementedError("MANUAL: match summary")
