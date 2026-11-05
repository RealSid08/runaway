"""Incident tools for the planning advisor.

A thin MCP (stdio) wrapper over the simulator's loopback API. Every tool returns observations or
what-if results built from observations; none of them expose ground truth.
"""

from __future__ import annotations

import os
import sys
from typing import Literal

import httpx
from mcp.server.mcpserver import MCPServer
from pydantic import BaseModel, Field

API = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("RUNAWAY_API", "http://127.0.0.1:7377")
mcp = MCPServer("incident")
client = httpx.Client(base_url=API, timeout=80)


class Action(BaseModel):
    type: Literal["cool", "stage", "interior_attack", "shelter", "evacuate"]
    unit: str | None = Field(None, description="Unit id, e.g. P1, A1. Required for cool, stage, interior_attack.")
    container: str | None = Field(None, description="Enclosure id A1-A6 or B1-B6, for cool and interior_attack.")
    staging: Literal["north", "west", "south", "east"] | None = Field(None, description="Gate, for stage.")
    receptor: Literal["homes", "school", "farm"] | None = Field(None, description="Community, for shelter and evacuate.")
    reason: str | None = None


def _get(path: str) -> str:
    return client.get(path).text


@mcp.tool()
def get_situation() -> str:
    """Current incident picture: time, decision trigger, wind readings, every enclosure's sensors, units, communities and recent events."""
    return _get("/situation")


@mcp.tool()
def get_sensor_check() -> str:
    """Cross-checks independent sensors: BMS against the thermal camera, gas detectors against HAZMAT, anemometer against smoke drift. Flags anything suspicious."""
    return _get("/sensors")


@mcp.tool()
def get_forecast() -> str:
    """Graph neural network forecast of each uninvolved enclosure's probability of thermal runaway within 10 and 20 minutes, from current observations."""
    return _get("/forecast")


@mcp.tool()
def test_plan(
    actions: list[Action],
    assume_wind_from_deg: float | None = None,
    assume_temps: dict[str, float] | None = None,
    horizon_min: int = 20,
) -> str:
    """Simulates a candidate plan 16 times from the current belief and compares it with keeping current orders.

    Returns enclosures involved at the horizon, per-enclosure runaway probability, indoor HF at each
    community, deflagration risk, crew exposure, and safety cautions. Use assume_wind_from_deg or
    assume_temps (e.g. {"A3": 120}) to test under a corrected assumption when a sensor looks wrong.
    An empty actions list tests current orders against themselves.
    """
    body = {
        "actions": [a.model_dump() for a in actions],
        "assume_wind_from_deg": assume_wind_from_deg,
        "assume_temps": assume_temps or {},
        "horizon_min": horizon_min,
    }
    return client.post("/test_plan", json=body).text


@mcp.tool()
def read_lessons() -> str:
    """Lessons written after earlier exercises, with ids to cite when one changes the plan."""
    return _get("/lessons")


if __name__ == "__main__":
    mcp.run()
