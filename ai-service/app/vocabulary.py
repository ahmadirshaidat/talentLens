"""Shared vocabulary for rule-based parsing: skills and spoken languages (Arabic + English).

Used by the query parser (to spot skills/languages in a recruiter query) and by the
fallback profile extractor (when no LLM is configured). Each canonical name maps to
the spellings people actually write, including Arabic ones.
"""

import re

# canonical skill -> aliases. All lowercase; Arabic already normalized
# (ا not أ/إ/آ, ه not ة, ي not ى).
SKILLS: dict[str, list[str]] = {
    "Python": ["python", "بايثون"],
    "Java": ["java", "جافا"],
    "JavaScript": ["javascript", "js", "جافاسكربت"],
    "TypeScript": ["typescript", "ts"],
    "C#": ["c#", "csharp", "c sharp"],
    "C++": ["c++", "cpp"],
    "Go": ["golang"],
    "PHP": ["php"],
    "Ruby": ["ruby"],
    "Kotlin": ["kotlin"],
    "Swift": ["swift"],
    "SQL": ["sql"],
    ".NET": [".net", "dotnet", "asp.net", "asp.net core", "دوت نت"],
    "React": ["react", "react.js", "reactjs", "رياكت"],
    "Angular": ["angular"],
    "Vue": ["vue", "vue.js"],
    "Node.js": ["node", "node.js", "nodejs"],
    "Django": ["django"],
    "Flask": ["flask"],
    "FastAPI": ["fastapi"],
    "Spring": ["spring", "spring boot"],
    "Laravel": ["laravel"],
    "SQL Server": ["sql server", "mssql"],
    "PostgreSQL": ["postgresql", "postgres"],
    "MySQL": ["mysql"],
    "MongoDB": ["mongodb", "mongo"],
    "Redis": ["redis"],
    "Docker": ["docker", "دوكر"],
    "Kubernetes": ["kubernetes", "k8s"],
    "AWS": ["aws", "amazon web services"],
    "Azure": ["azure"],
    "GCP": ["gcp", "google cloud"],
    "Git": ["git", "github", "gitlab"],
    "Linux": ["linux", "لينكس"],
    "Machine Learning": ["machine learning", "ml", "تعلم الاله"],
    "Deep Learning": ["deep learning", "التعلم العميق"],
    "NLP": ["nlp", "natural language processing", "معالجه اللغات الطبيعيه"],
    "Data Analysis": ["data analysis", "تحليل البيانات"],
    "Power BI": ["power bi", "powerbi"],
    "Excel": ["excel", "اكسل"],
    "Tableau": ["tableau"],
    "Pandas": ["pandas"],
    "TensorFlow": ["tensorflow"],
    "PyTorch": ["pytorch"],
    "Figma": ["figma"],
    "Photoshop": ["photoshop", "فوتوشوب"],
    "Flutter": ["flutter", "فلاتر"],
    "Android": ["android", "اندرويد"],
    "iOS": ["ios"],
    "REST APIs": ["rest", "rest api", "rest apis", "restful"],
    "Microservices": ["microservices", "microservice"],
    "Agile": ["agile", "scrum"],
    "Project Management": ["project management", "اداره المشاريع", "اداره مشاريع"],
    "Sales": ["sales", "مبيعات", "المبيعات"],
    "Marketing": ["marketing", "digital marketing", "تسويق", "التسويق", "التسويق الرقمي"],
    "SEO": ["seo"],
    "Accounting": ["accounting", "accountant", "محاسب", "محاسبه", "المحاسبه"],
    "Customer Service": ["customer service", "خدمه العملاء"],
    "Recruitment": ["recruitment", "recruiting", "توظيف", "التوظيف"],
    "Communication": ["communication", "التواصل", "مهارات التواصل"],
    "Leadership": ["leadership", "القياده"],
}

# canonical language -> aliases
LANGUAGES: dict[str, list[str]] = {
    "Arabic": ["arabic", "عربي", "العربيه", "اللغه العربيه", "عربيه"],
    "English": ["english", "انجليزي", "الانجليزيه", "اللغه الانجليزيه", "انجليزيه", "الانكليزيه"],
    "French": ["french", "فرنسي", "الفرنسيه", "اللغه الفرنسيه"],
    "German": ["german", "الماني", "الالمانيه", "اللغه الالمانيه"],
    "Spanish": ["spanish", "الاسبانيه", "اسباني"],
    "Turkish": ["turkish", "التركيه", "تركي"],
}

_AR_MAP = str.maketrans({"أ": "ا", "إ": "ا", "آ": "ا", "ة": "ه", "ى": "ي"})
_DIACRITICS = re.compile(r"[ً-ْـ]")  # harakat + tatweel


def normalize(text: str) -> str:
    """Lowercase, drop Arabic diacritics/tatweel, unify alef/taa-marbuta/ya spellings."""
    return _DIACRITICS.sub("", text.lower()).translate(_AR_MAP)


def _alias_pattern(alias: str) -> re.Pattern[str]:
    # Word-ish boundaries that also work for "c#", "c++", ".net" and Arabic.
    # Arabic words often carry a leading "و"/"ب"/"ل" (and / with / for), so allow one.
    prefix = r"(?:[وبل])?" if re.match(r"[؀-ۿ]", alias) else ""
    return re.compile(rf"(?<![\w.+#]){prefix}{re.escape(alias)}(?![\w+#])")


_SKILL_PATTERNS = [(name, [_alias_pattern(a) for a in al]) for name, al in SKILLS.items()]
_LANGUAGE_PATTERNS = [(name, [_alias_pattern(a) for a in al]) for name, al in LANGUAGES.items()]


def _find(text: str, patterns: list[tuple[str, list[re.Pattern[str]]]]) -> list[str]:
    norm = normalize(text)
    return [name for name, pats in patterns if any(p.search(norm) for p in pats)]


def find_skills(text: str) -> list[str]:
    """Canonical skill names mentioned in `text`, in vocabulary order."""
    return _find(text, _SKILL_PATTERNS)


def find_languages(text: str) -> list[str]:
    """Canonical spoken-language names mentioned in `text`."""
    return _find(text, _LANGUAGE_PATTERNS)


def canonical_skill(name: str) -> str:
    """Map a free-text skill (e.g. from the LLM) to its canonical name when we know it."""
    found = find_skills(name)
    return found[0] if len(found) == 1 else name.strip()


def canonical_language(name: str) -> str:
    found = find_languages(name)
    return found[0] if found else name.strip()
