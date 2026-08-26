from pydantic import BaseModel, ConfigDict

from ..profile import MashProfile


_STORY_MARKERS = (
    "剧情",
    "发生",
    "奥尔良",
    "特异点",
    "异闻带",
    "黑色枪管",
    "第几章",
    "战斗",
)


class QueryRoute(BaseModel):
    model_config = ConfigDict(extra="forbid")

    route: str
    reasons: tuple[str, ...]


def route_query(query: str, profile: MashProfile) -> QueryRoute:
    matched = tuple(marker for marker in _STORY_MARKERS if marker in query)
    if matched:
        return QueryRoute(route="story", reasons=matched)
    profile_terms = tuple(
        key for key, fact in profile.facts.items() if fact.value and fact.value in query
    )
    return QueryRoute(
        route="profile",
        reasons=profile_terms or ("no_explicit_story_intent",),
    )
