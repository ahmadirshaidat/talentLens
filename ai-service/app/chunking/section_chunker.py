"""manual - cv section"""
import re
from app.models import Chunk , CVSection

SECTION_HEADINGS: dict[str, list[str]] = {
    "summary": [
        "summary", "professional summary", "career summary", "profile",
        "professional profile", "about me", "about", "objective", "career objective",
        "نبذه", "نبذه عني", "نبذه شخصيه", "الملخص", "الملخص المهني", "الملخص الشخصي",
        "الهدف الوظيفي", "الهدف المهني", "الملف الشخصي",
    ],
    "experience": [
        "experience", "work experience", "professional experience", "employment history",
        "work history", "career history", "employment", "relevant experience",
        "internships", "internship",
        "الخبرات", "الخبره", "الخبرات العمليه", "الخبره العمليه", "الخبرات المهنيه",
        "الخبره المهنيه", "الخبرات الوظيفيه", "السجل الوظيفي", "التدريب العملي",
    ],
    "education": [
        "education", "academic background", "academic qualifications", "qualifications",
        "educational background", "academic history",
        "التعليم", "المؤهلات", "المؤهلات العلميه", "المؤهلات الاكاديميه",
        "المؤهل العلمي", "التحصيل العلمي", "الدراسه", "الخلفيه الاكاديميه",
    ],
    "skills": [
        "skills", "technical skills", "core skills", "key skills", "core competencies",
        "competencies", "soft skills", "hard skills", "technologies", "tech stack",
        "tools and technologies", "tools & technologies",
        "المهارات", "المهارات التقنيه", "المهارات الفنيه", "المهارات الشخصيه",
        "المهارات الحاسوبيه", "الكفاءات", "المهارات والقدرات",
    ],
    "projects": [
        "projects", "personal projects", "academic projects", "key projects",
        "selected projects", "portfolio",
        "المشاريع", "المشاريع الشخصيه", "المشاريع الاكاديميه", "مشاريع التخرج",
        "مشروع التخرج", "الاعمال",
    ],
    "certifications": [
        "certifications", "certificates", "licenses and certifications", "courses",
        "training", "trainings", "training courses", "awards", "achievements",
        "الشهادات", "الشهادات المهنيه", "الدورات", "الدورات التدريبيه",
        "الدورات والشهادات", "الجوائز", "الانجازات",
    ],
    "languages": [
        "languages", "language skills", "language proficiency",
        "اللغات", "المهارات اللغويه", "اللغه",
    ],
}

_AR_MAP = str.maketrans({
     "أ": "ا",
    "إ": "ا",
    "آ": "ا",
    "ة": "ه",
    "ى": "ي",
})
_DIACRITICS = re.compile(r"[\u064B-\u0652]")
_STRIP_CHARS = " :-•*#|.)(0123456789\t"

def _normalize(line: str)->str:
    s=line.lower()
    s=_DIACRITICS.sub("",s)
    s = s.translate(_AR_MAP)
    s = s.strip(_STRIP_CHARS)
    s = re.sub(r"\s+", " ", s)
    return s.strip()

def _match_heading(line:str)->str| None:
    s = _normalize(line)
    if s=="":
        return None
    if 5< len(s.split()) :
        return None
    for name,headings in SECTION_HEADINGS.items():
        for h in headings :
            if s ==_normalize(h):
                return name
    return None
        
   

def detect_sections(text:str) -> list[CVSection]:
    """Split raw CV text into sections by detecting headings.

    Args:
        text: Full CV text from the parser (line breaks preserved).

    Returns:
        Sections in document order. `name` is one of "header", "summary", "experience",
        "education", "skills", "projects", "certifications", "languages", "other".
    """
    sections: list[CVSection] = []
    current_name = "header"
    current_lines: list[str] = []

    for line in text.splitlines():
        name = _match_heading(line)
        if name is None:
            current_lines.append(line)
        else:
            body = "\n".join(current_lines).strip()
            if body:
                sections.append(CVSection(name=current_name, text=body))
            current_name = name
            current_lines = []

    body = "\n".join(current_lines).strip()
    if body:
        sections.append(CVSection(name=current_name, text=body))
    return sections

def _splite_text(text:str,max_chars:int)->list[str]:
    parts:list[str]=[]
    current = ""
    for line in text.splitlines():
        if current and len(current) +1+len(line)>max_chars:
            parts.append(current)
            current=line
        elif current:
            current = current + "\n" +line
        else :
            current = line
    if current:
        parts.append(current)
    return parts

def chunk_sections(sections:list[CVSection],candidate_id:str,workspace_id :str ,max_chars:int=1200)-> list[Chunk]:
    """Turn detected sections into chunks ready for embedding and BM25.

    Args:
        sections: Output of `detect_sections`.
        candidate_id: Owner candidate.
        workspace_id: Tenant scope.
        max_chars: Sections longer than this are split on line boundaries.

    Returns:
        Chunks with unique `chunk_id`s, each tagged with its section name.
    """
    chunks: list[Chunk] = []
    for section in sections:
        for part in _splite_text(section.text, max_chars):
            chunks.append(
                Chunk(
                    chunk_id=f"{candidate_id}_{len(chunks)}",
                    candidate_id=candidate_id,
                    workspace_id=workspace_id,
                    section=section.name,
                    text=part,
                )
            )
    return chunks
    