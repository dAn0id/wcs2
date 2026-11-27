#!/usr/bin/env python3
"""
WCS2 Race Builder — Semi-graphical (curses) TUI & CLI tool for creating,
editing, and validating Warcraft:Source 2 (WCS2) race JSON files.

Zero external dependencies (Python 3 standard library only).
"""

from __future__ import annotations

import argparse
import copy
import curses
import json
import math
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional


# =============================================================================
# Paths & Constants
# =============================================================================

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_RACES_DIR = SCRIPT_DIR

MIN_COLS = 64
MIN_ROWS = 18


# =============================================================================
# WCS2 Skill, Trigger, Target & Handler Catalog (1:1 with src/WCS2/Skills/)
# =============================================================================

@dataclass(frozen=True)
class FieldSpec:
    name: str
    kind: str  # "int", "float", "str", "bool", "color", "str_list", "choice"
    default: Any
    unit: str = ""
    description: str = ""
    choices: tuple[str, ...] = ()
    suggested_start: Optional[float] = None
    suggested_end: Optional[float] = None


@dataclass(frozen=True)
class PrimitiveSpec:
    key: str
    category: str
    summary: str
    default_target: str
    recommended_triggers: tuple[str, ...]
    level_specs: tuple[FieldSpec, ...] = ()
    param_specs: tuple[FieldSpec, ...] = ()


@dataclass(frozen=True)
class HandlerSpec:
    key: str
    attach_to: str
    summary: str
    default_trigger: str
    level_specs: tuple[FieldSpec, ...] = ()
    param_specs: tuple[FieldSpec, ...] = ()


RACE_TRIGGERS: list[tuple[str, str]] = [
    ("PlayerSpawn", "Runs when the player spawns alive (passive stats, buffs, weapons)"),
    ("PlayerAttacker", "Runs when the player deals weapon/grenade damage to an enemy"),
    ("PlayerVictim", "Runs when the player takes damage from an enemy (pre-damage)"),
    ("PlayerDeath", "Runs when the player dies (reincarnation, suicide explosion)"),
    ("PlayerKill", "Runs when the player kills an enemy"),
    ("PlayerUltimate", "Activated via `ultimate` / `css_ultimate` (or inspect bind)"),
    ("PlayerAbility", "Activated via `ability` / `css_ability` (e.g. wards)"),
    ("PlayerRoundStart", "Runs at the start of every round"),
    ("PlayerRoundEnd", "Runs at the end of every round"),
]

ALL_TRIGGERS_CANONICAL: dict[str, str] = {
    t.lower(): t for t, _ in RACE_TRIGGERS
} | {"playerbuy": "PlayerBuy"}

TARGETS: list[tuple[str, str]] = [
    ("self", "The skill owner (use for spawn buffs, self-heals, teleport, dash)"),
    ("victim", "The damaged player in combat (use on PlayerAttacker skills)"),
    ("attacker", "The enemy who hit you (use on PlayerVictim reflect skills)"),
    ("enemy", "Whichever combat participant is NOT the skill owner"),
    ("nearby_enemies", "All alive enemies within radius (for aoe_damage / aoe_freeze)"),
    ("nearby_teammates", "All alive teammates within radius excluding self (for instant_restock)"),
    ("all_teammates", "All alive teammates excluding self (for instant_restock)"),
    ("all", "All connected human players (for global `sound` primitive)"),
]

VALID_TARGET_KEYS: set[str] = {k for k, _ in TARGETS} | {"owner", "nearby_allies", "all_allies"}

STANDARD_CATEGORIES: list[str] = [
    "Standard races",
    "Custom races",
    "Knife races",
    "All races",
]

COMMON_WEAPONS: list[str] = [
    "weapon_knife",
    "weapon_c4",
    "weapon_p250",
    "weapon_deagle",
    "weapon_glock",
    "weapon_usp_silencer",
    "weapon_hkp2000",
    "weapon_m4a1",
    "weapon_m4a1_silencer",
    "weapon_ak47",
    "weapon_awp",
    "weapon_ssg08",
    "weapon_hegrenade",
    "weapon_flashbang",
    "weapon_smokegrenade",
    "weapon_molotov",
    "weapon_incgrenade",
    "weapon_decoy",
]

PRIMITIVE_CATEGORIES: list[str] = [
    "Health, Defense & Buffs",
    "Combat, Damage & Economy",
    "Control, Teleport & Respawn",
    "Weapons & Effects",
]

PRIMITIVES_LIST: list[PrimitiveSpec] = [
    # --- Health, Defense & Buffs (11) ---
    PrimitiveSpec(
        key="health",
        category="Health, Defense & Buffs",
        summary="Add or set HP (negative amount = HP penalty)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerKill", "PlayerUltimate"),
        level_specs=(
            FieldSpec("amount", "int", 20, "HP", "HP delta (add) or target HP (set)", suggested_start=10, suggested_end=50),
            FieldSpec("max_cap", "int", 0, "HP", "Max HP cap (0 = uncapped)"),
            FieldSpec("min_hp", "int", 1, "HP", "Minimum resulting HP floor"),
        ),
        param_specs=(
            FieldSpec("mode", "choice", "add", "", "add = delta, set = exact HP", ("add", "set")),
            FieldSpec("update_max_health", "bool", True, "", "Raise pawn MaxHealth if exceeded"),
            FieldSpec("message", "str", "", "", "Chat message ({amount}, {value})"),
        ),
    ),
    PrimitiveSpec(
        key="regen",
        category="Health, Defense & Buffs",
        summary="Periodic self or team-aura health regeneration",
        default_target="self",
        recommended_triggers=("PlayerSpawn",),
        level_specs=(
            FieldSpec("amount", "int", 3, "HP", "Fixed HP healed per tick", suggested_start=2, suggested_end=8),
            FieldSpec("min_heal", "int", 2, "HP", "Random min HP per tick", suggested_start=2, suggested_end=6),
            FieldSpec("max_heal", "int", 10, "HP", "Random max HP per tick"),
            FieldSpec("interval", "float", 2.0, "s", "Seconds between ticks"),
            FieldSpec("min_interval", "float", 5.0, "s", "Random min tick interval"),
            FieldSpec("max_interval", "float", 8.0, "s", "Random max tick interval"),
            FieldSpec("max_cap", "int", 175, "HP", "Heal cap (0 = capture spawn HP)"),
            FieldSpec("radius", "float", 0.0, "u", "0 = self only, >0 = self + nearby allies"),
        ),
        param_specs=(
            FieldSpec("tag", "str", "", "", "Unique timer tag (auto-generated if empty)"),
        ),
    ),
    PrimitiveSpec(
        key="evasion",
        category="Health, Defense & Buffs",
        summary="Passive (round) or timed attack dodge chance (0-100%)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate"),
        level_specs=(
            FieldSpec("chance", "float", 15.0, "%", "Dodge chance 0-100%", suggested_start=5, suggested_end=20),
            FieldSpec("duration", "float", 0.0, "s", "0 = full round, >0 = timed active buff", suggested_start=1.0, suggested_end=3.0),
            FieldSpec("warn_before_end", "float", 0.0, "s", "Seconds before expiry to warn player"),
        ),
        param_specs=(
            FieldSpec("message", "str", "", "", "Chat message ({chance}, {duration})"),
            FieldSpec("center_message", "str", "", "", "Center text when activated"),
            FieldSpec("fading_message", "str", "", "", "Warning before timed evasion fades"),
            FieldSpec("ended_message", "str", "", "", "Summary on expiry ({count} evaded)"),
        ),
    ),
    PrimitiveSpec(
        key="damage_absorb",
        category="Health, Defense & Buffs",
        summary="Passive incoming damage reduction (0.0-0.95) + optional mirror",
        default_target="self",
        recommended_triggers=("PlayerSpawn",),
        level_specs=(
            FieldSpec("absorb", "float", 0.10, "ratio", "Damage reduction fraction (0.05..0.95)", suggested_start=0.05, suggested_end=0.20),
            FieldSpec("mirror_min", "int", 0, "dmg", "Optional min mirror damage to attacker"),
            FieldSpec("mirror_max", "int", 0, "dmg", "Optional max mirror damage to attacker"),
        ),
        param_specs=(
            FieldSpec("message", "str", "", "", "Chat message ({pct})"),
        ),
    ),
    PrimitiveSpec(
        key="ultimate_immunity",
        category="Health, Defense & Buffs",
        summary="Grants immunity to enemy offensive ultimates for the round",
        default_target="self",
        recommended_triggers=("PlayerSpawn",),
        level_specs=(),
        param_specs=(
            FieldSpec("message", "str", "You are immune to enemy ultimates this round.", "", "Chat notification"),
        ),
    ),
    PrimitiveSpec(
        key="invisibility",
        category="Health, Defense & Buffs",
        summary="Sets render alpha (0=invisible, 255=solid) on pawn & weapons",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate"),
        level_specs=(
            FieldSpec("alpha", "int", 180, "0-255", "Render alpha (lower = more invisible)", suggested_start=230, suggested_end=140),
            FieldSpec("duration", "float", 0.0, "s", "0 = full round, >0 = timed cloak"),
        ),
        param_specs=(
            FieldSpec("r", "int", 255, "0-255", "Red tint"),
            FieldSpec("g", "int", 255, "0-255", "Green tint"),
            FieldSpec("b", "int", 255, "0-255", "Blue tint"),
            FieldSpec("message", "str", "", "", "Chat message ({pct})"),
        ),
    ),
    PrimitiveSpec(
        key="speed",
        category="Health, Defense & Buffs",
        summary="Set or add movement speed (spawn sprint or timed slow/boost)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerAttacker", "PlayerVictim", "PlayerUltimate"),
        level_specs=(
            FieldSpec("value", "float", 1.20, "x", "Multiplier (set) or delta (add)", suggested_start=1.10, suggested_end=1.40),
            FieldSpec("duration", "float", 0.0, "s", "0 = full round, >0 = temporary", suggested_start=1.0, suggested_end=3.0),
        ),
        param_specs=(
            FieldSpec("mode", "choice", "set", "", "set = multiplier, add = additive bonus", ("set", "add")),
            FieldSpec("source", "str", "", "", "Modifier source key (defaults to skill key)"),
            FieldSpec("prevent_stack_key", "str", "", "", "Guard key to prevent stacking (e.g. is_slowed)"),
            FieldSpec("message", "str", "", "", "Owner message ({pct}, {value}, {duration})"),
            FieldSpec("victim_message", "str", "", "", "Victim message when targeting enemy"),
        ),
    ),
    PrimitiveSpec(
        key="gravity",
        category="Health, Defense & Buffs",
        summary="Set or reduce gravity scale (0.1-2.0; preserved after ladders)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate"),
        level_specs=(
            FieldSpec("value", "float", 0.70, "x", "Gravity scale (set) or reduction (subtract)", suggested_start=0.90, suggested_end=0.40),
            FieldSpec("duration", "float", 0.0, "s", "0 = full round, >0 = temporary"),
        ),
        param_specs=(
            FieldSpec("mode", "choice", "set", "", "set = direct scale, subtract = 1.0 - value", ("set", "subtract")),
            FieldSpec("source", "str", "", "", "Modifier source key"),
            FieldSpec("message", "str", "", "", "Chat message ({pct}, {value})"),
        ),
    ),
    PrimitiveSpec(
        key="longjump",
        category="Health, Defense & Buffs",
        summary="Boosts horizontal jump velocity (clamped 0.1-2.5 for CS2)",
        default_target="self",
        recommended_triggers=("PlayerSpawn",),
        level_specs=(
            FieldSpec("multiplier", "float", 1.2, "x", "Horizontal jump velocity boost", suggested_start=0.5, suggested_end=2.0),
        ),
        param_specs=(
            FieldSpec("mode", "choice", "add", "", "add or set longjump modifier", ("add", "set")),
            FieldSpec("message", "str", "", "", "Chat message ({value})"),
        ),
    ),
    PrimitiveSpec(
        key="set_ultimate_cooldown",
        category="Health, Defense & Buffs",
        summary="Sets an initial cooldown on the race ultimate (e.g. on spawn)",
        default_target="self",
        recommended_triggers=("PlayerSpawn",),
        level_specs=(
            FieldSpec("seconds", "float", 5.0, "s", "Initial ultimate cooldown in seconds"),
        ),
        param_specs=(),
    ),
    PrimitiveSpec(
        key="give_levels",
        category="Health, Defense & Buffs",
        summary="Grants instant race levels (fixed or weighted bonus roll)",
        default_target="self",
        recommended_triggers=("PlayerKill", "PlayerSpawn"),
        level_specs=(
            FieldSpec("levels", "int", 1, "lvl", "Base levels granted"),
            FieldSpec("bonus_chance", "float", 0.0, "%", "Chance to grant bonus_levels instead"),
            FieldSpec("bonus_levels", "int", 2, "lvl", "Levels granted if bonus roll succeeds"),
        ),
        param_specs=(
            FieldSpec("message", "str", "", "", "Chat message ({value})"),
        ),
    ),
    PrimitiveSpec(
        key="player_scale",
        category="Health, Defense & Buffs",
        summary="Scales player model size (0.4-2.0x; resets to 1.0x on death/end)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("scale", "float", 0.8, "x", "Model scale (0.4-2.0)", suggested_start=0.9, suggested_end=0.6),
            FieldSpec("duration", "float", 0.0, "s", "0 = until death/round end, >0 = timed"),
        ),
        param_specs=(
            FieldSpec("message", "str", "", "", "Chat message ({value}, {pct}, {duration})"),
        ),
    ),
    PrimitiveSpec(
        key="infinite_ammo",
        category="Health, Defense & Buffs",
        summary="Chance on each weapon shot to refund the bullet into Clip1",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("chance", "float", 50.0, "%", "Bullet refund chance 0-100%", suggested_start=20, suggested_end=80),
            FieldSpec("duration", "float", 0.0, "s", "0 = until death/round end, >0 = timed"),
        ),
        param_specs=(
            FieldSpec("message", "str", "", "", "Chat message ({chance}, {duration})"),
        ),
    ),

    # --- Combat, Damage & Economy (10) ---
    PrimitiveSpec(
        key="bonus_damage",
        category="Combat, Damage & Economy",
        summary="Deals extra flat or random [min, max] damage on hit",
        default_target="victim",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(
            FieldSpec("damage", "float", 15.0, "dmg", "Flat extra damage", suggested_start=8, suggested_end=35),
            FieldSpec("min_damage", "int", 5, "dmg", "Random min extra damage", suggested_start=5, suggested_end=18),
            FieldSpec("max_damage", "int", 12, "dmg", "Random max extra damage", suggested_start=10, suggested_end=25),
        ),
        param_specs=(
            FieldSpec("ignore_armor", "bool", False, "", "Bypass armor mitigation"),
            FieldSpec("weapon_hint", "str", "", "", "Killfeed weapon hint (e.g. knife, hegrenade)"),
            FieldSpec("center_message", "str", "", "", "Attacker center text ({damage})"),
            FieldSpec("victim_message", "str", "", "", "Victim chat text ({damage})"),
        ),
    ),
    PrimitiveSpec(
        key="reflect_damage",
        category="Combat, Damage & Economy",
        summary="Reflects flat or random [min, max] damage back to attacker",
        default_target="attacker",
        recommended_triggers=("PlayerVictim",),
        level_specs=(
            FieldSpec("damage", "float", 10.0, "dmg", "Flat reflected damage"),
            FieldSpec("min_damage", "int", 5, "dmg", "Random min reflected damage", suggested_start=5, suggested_end=12),
            FieldSpec("max_damage", "int", 10, "dmg", "Random max reflected damage", suggested_start=8, suggested_end=15),
        ),
        param_specs=(
            FieldSpec("ignore_armor", "bool", False, "", "Bypass armor mitigation"),
            FieldSpec("center_message", "str", "", "", "Owner center text ({damage}, {attacker})"),
            FieldSpec("attacker_message", "str", "", "", "Attacker center text ({damage}, {victim})"),
        ),
    ),
    PrimitiveSpec(
        key="vampiric",
        category="Combat, Damage & Economy",
        summary="Heals attacker on hit up to max_cap HP (lifesteal)",
        default_target="self",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(
            FieldSpec("amount", "int", 15, "HP", "Flat HP leeched per proc"),
            FieldSpec("min_heal", "int", 12, "HP", "Random min HP leeched"),
            FieldSpec("max_heal", "int", 20, "HP", "Random max HP leeched"),
            FieldSpec("max_cap", "int", 260, "HP", "Health cap (0 = uncapped)"),
        ),
        param_specs=(
            FieldSpec("center_message", "str", "Leeched {heal} health!", "", "Center text ({heal})"),
            FieldSpec("cap_message", "str", "", "", "Message when already at health cap"),
        ),
    ),
    PrimitiveSpec(
        key="aoe_damage",
        category="Combat, Damage & Economy",
        summary="Deals AoE damage to nearby enemies (Chain Lightning / Suicide Bomb)",
        default_target="nearby_enemies",
        recommended_triggers=("PlayerUltimate", "PlayerDeath", "PlayerAttacker"),
        level_specs=(
            FieldSpec("radius", "float", 350.0, "u", "Effect radius in Hammer units", suggested_start=250, suggested_end=600),
            FieldSpec("damage", "float", 35.0, "dmg", "Flat damage per target", suggested_start=30, suggested_end=100),
            FieldSpec("min_damage", "int", 8, "dmg", "Random min damage per target"),
            FieldSpec("max_damage", "int", 19, "dmg", "Random max damage per target"),
            FieldSpec("max_targets", "int", 4, "cnt", "Max enemies struck", suggested_start=2, suggested_end=4),
        ),
        param_specs=(
            FieldSpec("respect_immunity", "bool", True, "", "Blocked by Ultimate Immunity"),
            FieldSpec("refund_if_none", "bool", True, "", "Refund cooldown if 0 enemies hit"),
            FieldSpec("draw_beams", "bool", False, "", "Draw beams from caster to targets"),
            FieldSpec("draw_rings", "bool", True, "", "Draw impact/AoE rings"),
            FieldSpec("beam_color", "color", [160, 160, 230], "RGB", "Beam RGB color"),
            FieldSpec("ring_color", "color", [255, 120, 40], "RGB", "Ring RGB color"),
            FieldSpec("weapon_hint", "str", "hegrenade", "", "Killfeed weapon hint"),
            FieldSpec("sound", "str", "", "", "Optional sound event"),
            FieldSpec("center_message", "str", "", "", "Owner center text ({count}, {extra})"),
            FieldSpec("target_message", "str", "", "", "Struck enemy center text ({value})"),
        ),
    ),
    PrimitiveSpec(
        key="burn_dot",
        category="Combat, Damage & Economy",
        summary="Periodic burn/poison DoT ticks attributed to attacker",
        default_target="victim",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(
            FieldSpec("duration", "float", 3.0, "s", "Total DoT duration", suggested_start=2.0, suggested_end=5.0),
            FieldSpec("damage_per_tick", "float", 4.0, "dmg", "Damage dealt each tick", suggested_start=3, suggested_end=8),
            FieldSpec("interval", "float", 0.5, "s", "Seconds between ticks (min 0.25s)"),
        ),
        param_specs=(
            FieldSpec("prevent_stack_key", "str", "is_burning", "", "Guard key to prevent stacking"),
            FieldSpec("color", "color", [232, 111, 0], "RGB", "Beam & tick ring RGB color"),
            FieldSpec("attacker_message", "str", "", "", "Attacker chat message ({victim}, {duration})"),
            FieldSpec("victim_message", "str", "", "", "Victim chat message ({attacker}, {duration})"),
        ),
    ),
    PrimitiveSpec(
        key="steal_cash",
        category="Combat, Damage & Economy",
        summary="Steals cash from victim (clamped to balance) and gives to attacker",
        default_target="victim",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(
            FieldSpec("amount", "int", 100, "$", "Max cash stolen per proc", suggested_start=50, suggested_end=200),
        ),
        param_specs=(
            FieldSpec("attacker_message", "str", "You stole ${amount} from {victim}!", "", "Attacker chat msg"),
            FieldSpec("victim_message", "str", "{attacker} stole ${amount} from you!", "", "Victim chat msg"),
        ),
    ),
    PrimitiveSpec(
        key="cash_to_health",
        category="Combat, Damage & Economy",
        summary="Spend cash to heal self if HP <= threshold (refunds CD if invalid)",
        default_target="self",
        recommended_triggers=("PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("amount", "int", 20, "HP", "HP healed", suggested_start=15, suggested_end=30),
            FieldSpec("cost", "int", 100, "$", "Cash cost per cast"),
            FieldSpec("max_hp_threshold", "int", 175, "HP", "Cannot cast above this HP"),
        ),
        param_specs=(),
    ),
    PrimitiveSpec(
        key="random_enemy_drain",
        category="Combat, Damage & Economy",
        summary="Strikes a random alive non-immune enemy; heals caster + grants armor",
        default_target="self",
        recommended_triggers=("PlayerUltimate",),
        level_specs=(
            FieldSpec("damage", "float", 35.0, "dmg", "Damage dealt to random enemy"),
            FieldSpec("heal", "int", 35, "HP", "HP restored to caster"),
            FieldSpec("armor", "int", 20, "AP", "Armor granted to caster", suggested_start=5, suggested_end=40),
            FieldSpec("health_cap", "int", 225, "HP", "Max caster HP cap"),
            FieldSpec("armor_cap", "int", 250, "AP", "Max caster armor cap"),
        ),
        param_specs=(),
    ),
    PrimitiveSpec(
        key="cheat_death",
        category="Combat, Damage & Economy",
        summary="Prevents a lethal hit, leaving owner at survive_hp + brief invuln",
        default_target="self",
        recommended_triggers=("PlayerVictim",),
        level_specs=(
            FieldSpec("survive_hp", "int", 1, "HP", "HP left after cheating death", suggested_start=1, suggested_end=25),
            FieldSpec("chance", "float", 100.0, "%", "Optional action-level chance 0-100%"),
            FieldSpec("invuln_duration", "float", 0.35, "s", "Brief godmode to survive multi-hits"),
        ),
        param_specs=(
            FieldSpec("max_per_round", "int", 1, "cnt", "Max procs per round (0 = unlimited)"),
            FieldSpec("message", "str", "Cheat Death saved you with {value} HP!", "", "Center text ({value})"),
        ),
    ),
    PrimitiveSpec(
        key="splash_damage",
        category="Combat, Damage & Economy",
        summary="Deals % of hit damage to nearby enemies around victim (excludes victim)",
        default_target="victim",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(
            FieldSpec("percent", "float", 35.0, "%", "Percent of hit damage splashed", suggested_start=15, suggested_end=50),
            FieldSpec("radius", "float", 200.0, "u", "Splash radius around victim", suggested_start=150, suggested_end=260),
        ),
        param_specs=(
            FieldSpec("min_damage", "float", 1.0, "dmg", "Minimum splash damage floor"),
            FieldSpec("max_damage", "float", 0.0, "dmg", "Max splash damage cap (0 = uncapped)"),
            FieldSpec("falloff", "bool", False, "", "Linear distance falloff across radius"),
            FieldSpec("weapon_hint", "str", "", "", "Killfeed weapon hint"),
            FieldSpec("ring_color", "color", [255, 140, 40], "RGB", "Optional impact ring RGB"),
        ),
    ),
    PrimitiveSpec(
        key="aim_explosion",
        category="Combat, Damage & Economy",
        summary="Detonates an AoE blast beneath the enemy in your crosshair (LOS + cone)",
        default_target="self",
        recommended_triggers=("PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("damage", "float", 60.0, "dmg", "Base explosion damage at center", suggested_start=35, suggested_end=70),
            FieldSpec("radius", "float", 220.0, "u", "Explosion AoE radius", suggested_start=160, suggested_end=300),
            FieldSpec("max_distance", "float", 1500.0, "u", "Max crosshair lock-on range"),
        ),
        param_specs=(
            FieldSpec("min_damage", "float", 10.0, "dmg", "Minimum falloff damage floor"),
            FieldSpec("aim_cone_degrees", "float", 8.0, "deg", "Crosshair lock-on cone half-angle"),
            FieldSpec("require_los", "bool", True, "", "Require clear line-of-sight to target"),
            FieldSpec("impact_particle_1", "str", "particles/survival_fx/gas_cannister_impact.vpcf", "", "Primary impact particle .vpcf"),
            FieldSpec("impact_particle_2", "str", "particles/ui/ui_electric_exp_glow.vpcf", "", "Secondary impact particle .vpcf"),
            FieldSpec("sound", "str", "UIPanorama.equip_musicKit", "", "Sound event emitted on blast"),
            FieldSpec("message", "str", "", "", "Optional center message ({damage}, {count})"),
        ),
    ),

    # --- Control, Teleport & Respawn (10) ---
    PrimitiveSpec(
        key="freeze",
        category="Control, Teleport & Respawn",
        summary="Immobilizes (MOVETYPE_NONE) a single target for duration",
        default_target="victim",
        recommended_triggers=("PlayerAttacker", "PlayerVictim"),
        level_specs=(
            FieldSpec("duration", "float", 1.0, "s", "Freeze duration in seconds", suggested_start=0.8, suggested_end=2.0),
        ),
        param_specs=(
            FieldSpec("prevent_stack", "bool", True, "", "Skip if target is already frozen"),
            FieldSpec("center_message", "str", "Immobilized {victim} for {duration}s!", "", "Owner center text"),
            FieldSpec("victim_message", "str", "You were frozen by {attacker} for {duration}s!", "", "Victim center text"),
        ),
    ),
    PrimitiveSpec(
        key="aoe_freeze",
        category="Control, Teleport & Respawn",
        summary="Freezes all alive non-immune enemies within radius (refunds CD if 0)",
        default_target="nearby_enemies",
        recommended_triggers=("PlayerUltimate",),
        level_specs=(
            FieldSpec("radius", "float", 300.0, "u", "Freeze radius in Hammer units", suggested_start=225, suggested_end=400),
            FieldSpec("duration", "float", 2.5, "s", "Freeze duration", suggested_start=2.0, suggested_end=3.0),
            FieldSpec("max_targets", "int", 20, "cnt", "Max enemies frozen"),
        ),
        param_specs=(
            FieldSpec("respect_immunity", "bool", True, "", "Blocked by Ultimate Immunity"),
            FieldSpec("beam_color", "color", [0, 160, 0], "RGB", "Beam RGB color"),
            FieldSpec("ring_color", "color", [0, 180, 0], "RGB", "Ring RGB color"),
            FieldSpec("sound", "str", "", "", "Optional sound event"),
        ),
    ),
    PrimitiveSpec(
        key="push",
        category="Control, Teleport & Respawn",
        summary="Applies a velocity impulse to target (e.g. upward Impale launch)",
        default_target="victim",
        recommended_triggers=("PlayerAttacker", "PlayerVictim", "PlayerUltimate"),
        level_specs=(
            FieldSpec("vx", "float", 0.0, "u/s", "X velocity impulse"),
            FieldSpec("vy", "float", 0.0, "u/s", "Y velocity impulse"),
            FieldSpec("vz", "float", 520.0, "u/s", "Z upward launch impulse", suggested_start=300, suggested_end=600),
        ),
        param_specs=(
            FieldSpec("additive", "bool", True, "", "Add to current velocity instead of replacing"),
        ),
    ),
    PrimitiveSpec(
        key="shake",
        category="Control, Teleport & Respawn",
        summary="Sends a CS2 Shake screen shake to the target",
        default_target="victim",
        recommended_triggers=("PlayerAttacker", "PlayerVictim"),
        level_specs=(
            FieldSpec("amplitude", "float", 15.0, "", "Shake amplitude (1-30)"),
            FieldSpec("frequency", "float", 13.0, "Hz", "Shake frequency"),
            FieldSpec("duration", "float", 3.0, "s", "Shake duration", suggested_start=1.5, suggested_end=3.5),
        ),
        param_specs=(
            FieldSpec("attacker_message", "str", "", "", "Owner center text ({victim})"),
            FieldSpec("victim_message", "str", "", "", "Victim center text ({attacker})"),
        ),
    ),
    PrimitiveSpec(
        key="fade",
        category="Control, Teleport & Respawn",
        summary="Sends a CS2 Fade screen tint + optional shake (Banish effect)",
        default_target="victim",
        recommended_triggers=("PlayerAttacker", "PlayerVictim"),
        level_specs=(
            FieldSpec("duration", "float", 1.5, "s", "Total fade duration", suggested_start=1.0, suggested_end=3.0),
            FieldSpec("shake_amplitude", "float", 0.0, "", "Optional simultaneous shake amplitude"),
            FieldSpec("shake_frequency", "float", 8.0, "Hz", "Optional shake frequency"),
        ),
        param_specs=(
            FieldSpec("r", "int", 60, "0-255", "Fade red"),
            FieldSpec("g", "int", 200, "0-255", "Fade green"),
            FieldSpec("b", "int", 210, "0-255", "Fade blue"),
            FieldSpec("a", "int", 175, "0-255", "Fade alpha opacity"),
            FieldSpec("hold_ratio", "float", 0.7, "0-1", "Portion of duration held solid"),
            FieldSpec("attacker_message", "str", "", "", "Owner chat text ({victim}, {duration})"),
            FieldSpec("victim_message", "str", "", "", "Victim chat text ({attacker}, {duration})"),
        ),
    ),
    PrimitiveSpec(
        key="teleport",
        category="Control, Teleport & Respawn",
        summary="Safe hull-traced crosshair teleport (refunds CD if blocked)",
        default_target="self",
        recommended_triggers=("PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("range", "float", 2000.0, "u", "Max teleport distance", suggested_start=1200, suggested_end=4000),
            FieldSpec("push_factor", "float", 1.8, "x", "Velocity factor when mode=push"),
        ),
        param_specs=(
            FieldSpec("mode", "choice", "safe", "", "safe = hull-traced teleport, push = velocity launch", ("safe", "push")),
            FieldSpec("sound", "str", "", "", "Optional sound event"),
        ),
    ),
    PrimitiveSpec(
        key="teleport_dash",
        category="Control, Teleport & Respawn",
        summary="Dash-style forward velocity launch + electric/distort particles",
        default_target="self",
        recommended_triggers=("PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("force", "float", 1000.0, "u/s", "Forward launch speed (200-2500)", suggested_start=700, suggested_end=1400),
            FieldSpec("z_boost", "float", 150.0, "u/s", "Upward Z velocity boost"),
        ),
        param_specs=(
            FieldSpec("sound", "str", "UIPanorama.equip_musicKit", "", "CS2 sound event"),
            FieldSpec("sound_volume", "float", 0.5, "0-1", "Sound volume (0.05-1.0)"),
            FieldSpec("particle_count", "int", 2, "cnt", "Particle pairs spawned (0-4)"),
            FieldSpec("glow_particle", "str", "particles/ui/ui_electric_exp_glow.vpcf", "", "Glow particle .vpcf"),
            FieldSpec("glow_duration", "float", 3.0, "s", "Glow particle lifetime"),
            FieldSpec("distort_particle", "str", "particles/explosions_fx/explosion_smokegrenade_distort.vpcf", "", "Distort particle .vpcf"),
            FieldSpec("distort_duration", "float", 2.0, "s", "Distort particle lifetime"),
        ),
    ),
    PrimitiveSpec(
        key="air_dash",
        category="Control, Teleport & Respawn",
        summary="Passive midair double-jump directional dash (WASD + Jump in air)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("force", "float", 800.0, "u/s", "Dash velocity impulse (200-2500)", suggested_start=600, suggested_end=1100),
            FieldSpec("cooldown", "float", 4.0, "s", "Per-dash cooldown in seconds (0-60)", suggested_start=6.0, suggested_end=2.5),
            FieldSpec("duration", "float", 0.0, "s", "Active duration (0 = full round)"),
        ),
        param_specs=(
            FieldSpec("min_z", "float", 0.275, "0-1", "Minimum upward Z direction component"),
            FieldSpec("air_delay", "float", 0.12, "s", "Delay in air before Jump triggers dash"),
            FieldSpec("sound", "str", "Default.WalkJump", "", "CS2 sound event played on dash"),
            FieldSpec("sound_volume", "float", 0.5, "0-1", "Sound volume (0.05-1.0)"),
            FieldSpec("message", "str", "", "", "Optional chat message on dash"),
        ),
    ),
    PrimitiveSpec(
        key="respawn",
        category="Control, Teleport & Respawn",
        summary="Delayed self-respawn on death with countdown (round-guarded)",
        default_target="self",
        recommended_triggers=("PlayerDeath",),
        level_specs=(
            FieldSpec("delay", "float", 3.0, "s", "Seconds before respawning"),
        ),
        param_specs=(
            FieldSpec("at_death_origin", "bool", True, "", "Teleport back to death position"),
            FieldSpec("restore_weapons", "bool", False, "", "Restore weapons carried at death"),
            FieldSpec("countdown", "bool", True, "", "Show 3-2-1 center countdown"),
            FieldSpec("ring_color", "color", [255, 71, 36], "RGB", "Respawn marker ring RGB"),
        ),
    ),
    PrimitiveSpec(
        key="revive_teammate",
        category="Control, Teleport & Respawn",
        summary="Revives 1+ random dead teammates (refunds CD if none dead)",
        default_target="self",
        recommended_triggers=("PlayerUltimate", "PlayerAbility", "PlayerKill", "PlayerSpawn", "PlayerDeath"),
        level_specs=(
            FieldSpec("count", "int", 1, "cnt", "Number of dead teammates revived", suggested_start=1, suggested_end=2),
            FieldSpec("health", "int", 0, "HP", "Optional spawn HP (0 = default 100)"),
        ),
        param_specs=(
            FieldSpec("at_caster", "bool", False, "", "True = revive at caster, False = team spawn"),
        ),
    ),

    # --- Weapons & Effects (7) ---
    PrimitiveSpec(
        key="give_weapon",
        category="Weapons & Effects",
        summary="Gives one or more CS2 weapons (optional primary/secondary strip)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerKill", "PlayerUltimate"),
        level_specs=(),
        param_specs=(
            FieldSpec("weapon", "str", "", "", "Single weapon name (e.g. weapon_hegrenade)"),
            FieldSpec("weapons", "str_list", [], "", "List of weapons (e.g. weapon_deagle, weapon_m4a1)"),
            FieldSpec("strip_primary", "bool", False, "", "Strip existing guns before giving"),
            FieldSpec("strip_secondary", "bool", False, "", "Strip existing pistols before giving"),
            FieldSpec("message", "str", "", "", "Chat notification"),
        ),
    ),
    PrimitiveSpec(
        key="drop_weapon",
        category="Weapons & Effects",
        summary="Forces target to drop their primary (or active) weapon",
        default_target="victim",
        recommended_triggers=("PlayerAttacker",),
        level_specs=(),
        param_specs=(
            FieldSpec("primary_only", "bool", True, "", "Drop primary slot only"),
            FieldSpec("attacker_message", "str", "You disarmed {victim}!", "", "Owner center text"),
            FieldSpec("victim_message", "str", "You were forced to drop your weapon!", "", "Victim center text"),
        ),
    ),
    PrimitiveSpec(
        key="set_clip_ammo",
        category="Weapons & Effects",
        summary="Overrides weapon magazine clip and/or reserve ammo",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerKill", "PlayerUltimate"),
        level_specs=(
            FieldSpec("clip", "int", 30, "rnds", "Magazine clip count"),
            FieldSpec("reserve", "int", 90, "rnds", "Reserve ammo count"),
        ),
        param_specs=(
            FieldSpec("weapon", "str", "secondary", "", "Weapon classname or primary/secondary"),
        ),
    ),
    PrimitiveSpec(
        key="instant_restock",
        category="Weapons & Effects",
        summary="Restocks/grants a weapon on an interval or one-shot (self or teammates)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("interval", "float", 10.0, "s", "Restock interval in seconds (<=0 = single one-shot grant)", suggested_start=12.0, suggested_end=4.0),
            FieldSpec("duration", "float", 0.0, "s", "0 = until death/round end, >0 = stop after duration"),
            FieldSpec("max_grants", "int", 0, "cnt", "Max grants (0 = unlimited)"),
            FieldSpec("radius", "float", 300.0, "u", "Radius when target is nearby_teammates"),
        ),
        param_specs=(
            FieldSpec("weapon", "str", "weapon_hegrenade", "", "CS2 weapon classname (normalized automatically)"),
            FieldSpec("only_if_missing", "bool", True, "", "Only give if target doesn't already carry the weapon"),
            FieldSpec("immediate", "bool", True, "", "Grant immediately on trigger in addition to interval ticks"),
            FieldSpec("message", "str", "", "", "Optional chat message ({value} = weapon)"),
        ),
    ),
    PrimitiveSpec(
        key="explosive_molotov",
        category="Weapons & Effects",
        summary="Turns thrown Molotov/Incendiary into a high-speed exploding Fireball",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAbility"),
        level_specs=(
            FieldSpec("damage", "float", 80.0, "dmg", "Base AoE explosion damage at center", suggested_start=50, suggested_end=110),
            FieldSpec("radius", "float", 250.0, "u", "Explosion AoE radius", suggested_start=180, suggested_end=300),
            FieldSpec("speed", "float", 1800.0, "u/s", "Fireball launch speed (0 = keep normal grenade arc)"),
            FieldSpec("duration", "float", 0.0, "s", "0 = until death/round end, >0 = timed"),
        ),
        param_specs=(
            FieldSpec("min_damage", "float", 5.0, "dmg", "Minimum falloff damage floor"),
            FieldSpec("include_incendiary", "bool", True, "", "Also convert CT incendiary grenades"),
            FieldSpec("fireball_visuals", "bool", True, "", "Orange fireball model + fire trail particle"),
            FieldSpec("message", "str", "", "", "Optional chat message ({damage}, {duration})"),
        ),
    ),
    PrimitiveSpec(
        key="beam",
        category="Weapons & Effects",
        summary="Draws a colored CBeam line between two players",
        default_target="victim",
        recommended_triggers=("PlayerAttacker", "PlayerVictim"),
        level_specs=(
            FieldSpec("width", "float", 4.0, "u", "Beam thickness"),
            FieldSpec("duration", "float", 0.7, "s", "Beam lifetime"),
            FieldSpec("z_offset", "float", 25.0, "u", "Height offset from player feet"),
        ),
        param_specs=(
            FieldSpec("from", "choice", "attacker", "", "Start player", ("attacker", "victim", "self")),
            FieldSpec("to", "choice", "victim", "", "End player", ("victim", "attacker", "self")),
            FieldSpec("r", "int", 255, "0-255", "Red"),
            FieldSpec("g", "int", 255, "0-255", "Green"),
            FieldSpec("b", "int", 255, "0-255", "Blue"),
        ),
    ),
    PrimitiveSpec(
        key="beam_ring",
        category="Weapons & Effects",
        summary="Draws one or more stacked CBeam rings around a player",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerAttacker", "PlayerUltimate"),
        level_specs=(
            FieldSpec("radius", "float", 65.0, "u", "Ring radius"),
            FieldSpec("width", "float", 4.0, "u", "Ring thickness"),
            FieldSpec("duration", "float", 1.0, "s", "Ring lifetime"),
            FieldSpec("z_offset", "float", 15.0, "u", "Starting height offset"),
            FieldSpec("count", "int", 1, "cnt", "Number of stacked rings (1-6)"),
            FieldSpec("z_step", "float", 10.0, "u", "Vertical gap between stacked rings"),
        ),
        param_specs=(
            FieldSpec("r", "int", 255, "0-255", "Red"),
            FieldSpec("g", "int", 255, "0-255", "Green"),
            FieldSpec("b", "int", 255, "0-255", "Blue"),
        ),
    ),
    PrimitiveSpec(
        key="sound",
        category="Weapons & Effects",
        summary="Plays a CS2 sound event for a player or everyone (target=all)",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAttacker"),
        level_specs=(),
        param_specs=(
            FieldSpec("sound_event", "str", "UIPanorama.equip_musicKit", "", "CS2 sound event name"),
        ),
    ),
    PrimitiveSpec(
        key="message",
        category="Weapons & Effects",
        summary="Prints a formatted chat or center-screen message",
        default_target="self",
        recommended_triggers=("PlayerSpawn", "PlayerUltimate", "PlayerAttacker"),
        level_specs=(),
        param_specs=(
            FieldSpec("text", "str", "", "", "Message text ({owner}, {attacker}, {victim})"),
            FieldSpec("center", "bool", False, "", "True = center screen, False = chat"),
        ),
    ),
]

PRIMITIVES: dict[str, PrimitiveSpec] = {p.key: p for p in PRIMITIVES_LIST}

HANDLERS_LIST: list[HandlerSpec] = [
    HandlerSpec(
        key="archmage_lift_off",
        attach_to="PlayerUltimate skill",
        summary="Toggleable jetpack flight (humans only) + bonus HP while flying",
        default_trigger="PlayerUltimate",
        level_specs=(
            FieldSpec("bonus_hp", "int", 10, "HP", "Bonus HP while flying", suggested_start=5, suggested_end=20),
        ),
    ),
    HandlerSpec(
        key="blood_mage_phoenix",
        attach_to="PlayerSpawn skill",
        summary="Team-wide Phoenix revival pool (uses skill.chances, revives at +3s)",
        default_trigger="PlayerSpawn",
    ),
    HandlerSpec(
        key="shadow_hunter_ward",
        attach_to="Race + PlayerAbility skill",
        summary="Serpent Ward: 3/round stationary AoE damage + 30% slow + beam visuals",
        default_trigger="PlayerAbility",
        level_specs=(
            FieldSpec("duration", "float", 12.0, "s", "Ward lifetime", suggested_start=10, suggested_end=15),
            FieldSpec("damage", "float", 15.0, "dmg", "Damage per 1s pulse", suggested_start=10, suggested_end=20),
            FieldSpec("radius", "float", 160.0, "u", "Ward AoE radius", suggested_start=150, suggested_end=175),
        ),
        param_specs=(
            FieldSpec("charges", "int", 3, "cnt", "Wards available per round"),
        ),
    ),
    HandlerSpec(
        key="warden_restoration",
        attach_to="PlayerUltimate skill",
        summary="16-tier missing-HP scaling self-heal (refuses if HP >= 175)",
        default_trigger="PlayerUltimate",
    ),
    HandlerSpec(
        key="succubus_skulls",
        attach_to="Race + head_hunter / incantation / transformation",
        summary="Persistent 0-5 skull counter across rounds fueling spawn buffs & ultimate",
        default_trigger="PlayerUltimate",
        param_specs=(
            FieldSpec("gravity", "float", 0.50, "x", "Transformation gravity"),
            FieldSpec("bonus_hp", "int", 25, "HP", "Transformation HP gain"),
            FieldSpec("health_cap", "int", 200, "HP", "Transformation HP cap"),
            FieldSpec("bonus_speed", "float", 0.25, "x", "Transformation speed boost"),
            FieldSpec("speed_cap", "float", 2.0, "x", "Transformation speed cap"),
        ),
    ),
    HandlerSpec(
        key="defiladed_destructor",
        attach_to="Race + PlayerUltimate skill",
        summary="Oil-barrel prop disguise, P250 250-clip, -60 HP + Build Barrel spawner",
        default_trigger="PlayerUltimate",
        param_specs=(
            FieldSpec("max_props", "int", 10, "cnt", "Max active barrels per round"),
            FieldSpec("explosive", "bool", True, "", "Whether spawned barrels explode"),
            FieldSpec("explosion_damage", "float", 90.0, "dmg", "Explosion damage"),
            FieldSpec("explosion_radius", "float", 220.0, "u", "Explosion radius"),
            FieldSpec("lifetime", "float", 60.0, "s", "Barrel lifetime"),
            FieldSpec("model", "str", "", "", "Optional custom .vmdl (default from wcs.json)"),
        ),
    ),
]

CUSTOM_HANDLERS: dict[str, HandlerSpec] = {h.key: h for h in HANDLERS_LIST}


# =============================================================================
# JSONC Parsing, Interpolation, Formatting & Schema Validation
# =============================================================================

def slugify_key(text: str) -> str:
    """Convert a display name into a clean WCS2 snake_case key."""
    s = text.strip().lower()
    s = re.sub(r"[^a-z0-9]+", "_", s)
    s = re.sub(r"_+", "_", s).strip("_")
    return s or "custom_race"


def strip_jsonc_comments(text: str) -> str:
    """
    Strip // line comments, /* */ block comments, and trailing commas
    while preserving string literals untouched.
    """
    out: list[str] = []
    i = 0
    n = len(text)
    in_string = False
    escape = False

    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if escape:
                escape = False
            elif ch == "\\":
                escape = True
            elif ch == '"':
                in_string = False
            i += 1
            continue

        if ch == '"':
            in_string = True
            out.append(ch)
            i += 1
            continue

        # Line comment //
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            i += 2
            while i < n and text[i] != "\n":
                i += 1
            continue

        # Block comment /* ... */
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue

        out.append(ch)
        i += 1

    cleaned = "".join(out)

    # Remove trailing commas before ] or } outside strings
    final: list[str] = []
    in_string = False
    escape = False
    i = 0
    m = len(cleaned)
    while i < m:
        ch = cleaned[i]
        if in_string:
            final.append(ch)
            if escape:
                escape = False
            elif ch == "\\":
                escape = True
            elif ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True
            final.append(ch)
            i += 1
            continue
        if ch == ",":
            j = i + 1
            while j < m and cleaned[j] in " \t\r\n":
                j += 1
            if j < m and cleaned[j] in "]}":
                i += 1
                continue
        final.append(ch)
        i += 1

    return "".join(final)


def load_race_file(path: Path) -> dict[str, Any]:
    raw = path.read_text(encoding="utf-8")
    data = json.loads(strip_jsonc_comments(raw))
    if not isinstance(data, dict):
        raise ValueError("Race JSON root must be an object.")
    return normalize_race_dict(data)


def interpolate_levels(start: float, end: float, count: int, as_int: bool = False, decimals: int = 2) -> list[int | float]:
    """Linearly interpolate `count` values from `start` to `end`."""
    count = max(1, count)
    if count == 1:
        val = int(round(start)) if as_int else round(float(start), decimals)
        return [0 if val == 0 else val]

    result: list[int | float] = []
    for idx in range(count):
        t = idx / (count - 1)
        v = start + (end - start) * t
        if as_int:
            result.append(int(round(v)))
        else:
            rv = round(v, decimals)
            if rv == 0:
                rv = 0.0
            # Emit clean int if exact whole number and both inputs were whole
            if float(start).is_integer() and float(end).is_integer() and float(rv).is_integer():
                result.append(int(rv))
            else:
                result.append(rv)
    return result


def parse_number_list(raw: str, max_level: int, as_int: bool = False) -> list[int | float]:
    """
    Parse either:
      - Range shorthand: `10..50` or `1.1->1.45` (interpolated across max_level)
      - Comma/space list: `10, 20, 30, 40`
      - Single number: `15` -> `[15]`
    """
    s = raw.strip()
    if not s:
        return []

    range_match = re.fullmatch(r"\s*([+-]?\d+(?:\.\d+)?)\s*(?:\.\.|->)\s*([+-]?\d+(?:\.\d+)?)\s*", s)
    if range_match:
        a = float(range_match.group(1))
        b = float(range_match.group(2))
        return interpolate_levels(a, b, max(1, max_level), as_int=as_int)

    tokens = [tok for tok in re.split(r"[\s,;]+", s.strip("[]")) if tok]
    values: list[int | float] = []
    for tok in tokens:
        num = float(tok)
        if as_int:
            values.append(int(round(num)))
        elif num.is_integer() and "." not in tok:
            values.append(int(num))
        else:
            values.append(round(num, 4))
    return values


def new_empty_race() -> dict[str, Any]:
    return {
        "key": "custom_race",
        "name": "Custom Race",
        "author": "Admin",
        "description": "A custom WCS2 race.",
        "required_level": 0,
        "max_level": 0,
        "team_limit": 0,
        "restrict_team": 0,
        "categories": ["Custom races", "All races"],
        "allowed_weapons": [],
        "restricted_weapons": [],
        "spawn_actions": [],
        "death_actions": [],
        "round_start_actions": [],
        "round_end_actions": [],
        "custom_handler": None,
        "skills": [],
    }


def new_empty_skill(index: int = 1, trigger: str = "PlayerSpawn") -> dict[str, Any]:
    is_ult = trigger == "PlayerUltimate"
    return {
        "key": f"skill_{index}",
        "name": f"Skill {index}",
        "description": "Describe what this skill does.",
        "trigger": trigger,
        "max_level": 8,
        "required_level": 8 if is_ult else 0,
        "cooldowns": [15.0] if is_ult else [],
        "chances": [],
        "weapons": [],
        "custom_handler": None,
        "actions": [],
    }


def new_action_for_primitive(prim_key: str, trigger: str = "PlayerSpawn", max_level: int = 8) -> dict[str, Any]:
    spec = PRIMITIVES[prim_key]
    target = spec.default_target
    if trigger == "PlayerVictim" and target == "victim":
        target = "attacker"
    action: dict[str, Any] = {
        "type": prim_key,
        "target": target,
        "delay": 0.0,
        "level_values": {},
        "params": {},
    }
    # Pre-populate sensible starter defaults for the primary field(s)
    if spec.level_specs:
        first = spec.level_specs[0]
        if first.suggested_start is not None and first.suggested_end is not None:
            action["level_values"][first.name] = interpolate_levels(
                first.suggested_start,
                first.suggested_end,
                max(1, max_level),
                as_int=(first.kind == "int"),
            )
    # Helpful default params for certain primitives
    if prim_key == "teleport_dash":
        action["params"]["z_boost"] = 150
        action["params"]["sound"] = "UIPanorama.equip_musicKit"
        action["params"]["sound_volume"] = 0.5
        action["params"]["particle_count"] = 2
    elif prim_key == "teleport":
        action["params"]["mode"] = "safe"
    elif prim_key == "speed":
        action["params"]["mode"] = "set"
    elif prim_key == "gravity":
        action["params"]["mode"] = "set"
    elif prim_key == "health":
        action["params"]["mode"] = "add"
        action["params"]["update_max_health"] = True
    return action


def normalize_race_dict(data: dict[str, Any]) -> dict[str, Any]:
    """Ensure loaded race JSON has all expected keys with sane defaults."""
    base = new_empty_race()
    for k in (
        "key", "name", "author", "description", "required_level", "max_level",
        "team_limit", "restrict_team", "categories", "allowed_weapons",
        "restricted_weapons", "spawn_actions", "death_actions",
        "round_start_actions", "round_end_actions", "custom_handler",
    ):
        if k in data and data[k] is not None:
            base[k] = copy.deepcopy(data[k])

    skills: list[dict[str, Any]] = []
    for idx, raw_s in enumerate(data.get("skills", []) or [], start=1):
        if not isinstance(raw_s, dict):
            continue
        sk = new_empty_skill(idx)
        for k in (
            "key", "name", "description", "trigger", "max_level",
            "required_level", "cooldowns", "chances", "weapons",
            "custom_handler",
        ):
            if k in raw_s and raw_s[k] is not None:
                sk[k] = copy.deepcopy(raw_s[k])
        actions = []
        for raw_a in raw_s.get("actions", []) or []:
            if not isinstance(raw_a, dict):
                continue
            actions.append({
                "type": raw_a.get("type", ""),
                "target": raw_a.get("target", "self"),
                "delay": float(raw_a.get("delay", 0.0) or 0.0),
                "level_values": copy.deepcopy(raw_a.get("level_values", {}) or {}),
                "params": copy.deepcopy(raw_a.get("params", {}) or {}),
            })
        sk["actions"] = actions
        skills.append(sk)
    base["skills"] = skills
    return base


def clean_action_for_export(action: dict[str, Any]) -> dict[str, Any]:
    out: dict[str, Any] = {
        "type": action.get("type", ""),
        "target": action.get("target", "self") or "self",
    }
    delay = float(action.get("delay", 0.0) or 0.0)
    if delay > 0:
        out["delay"] = round(delay, 3)
    lv = action.get("level_values") or {}
    if lv:
        out["level_values"] = lv
    params = action.get("params") or {}
    if params:
        out["params"] = params
    return out


def clean_race_for_export(race: dict[str, Any]) -> dict[str, Any]:
    """Produce idiomatic WCS2 race JSON matching src/WCS2/configs/races/*.json."""
    out: dict[str, Any] = {
        "key": race.get("key", ""),
        "name": race.get("name", ""),
        "author": race.get("author", ""),
        "description": race.get("description", ""),
        "required_level": int(race.get("required_level", 0) or 0),
        "max_level": int(race.get("max_level", 0) or 0),
        "team_limit": int(race.get("team_limit", 0) or 0),
        "restrict_team": int(race.get("restrict_team", 0) or 0),
        "categories": list(race.get("categories") or ["All races"]),
    }
    if race.get("allowed_weapons"):
        out["allowed_weapons"] = list(race["allowed_weapons"])
    if race.get("restricted_weapons"):
        out["restricted_weapons"] = list(race["restricted_weapons"])
    if race.get("custom_handler"):
        out["custom_handler"] = race["custom_handler"]

    for lk in ("spawn_actions", "death_actions", "round_start_actions", "round_end_actions"):
        acts = race.get(lk) or []
        if acts:
            out[lk] = [clean_action_for_export(a) for a in acts]

    skills_out: list[dict[str, Any]] = []
    for sk in race.get("skills", []):
        s_out: dict[str, Any] = {
            "key": sk.get("key", ""),
            "name": sk.get("name", ""),
            "description": sk.get("description", ""),
            "trigger": sk.get("trigger", "PlayerSpawn"),
            "max_level": int(sk.get("max_level", 8) or 8),
            "required_level": int(sk.get("required_level", 0) or 0),
        }
        if sk.get("cooldowns"):
            s_out["cooldowns"] = list(sk["cooldowns"])
        if sk.get("chances"):
            s_out["chances"] = list(sk["chances"])
        if sk.get("weapons"):
            s_out["weapons"] = list(sk["weapons"])
        if sk.get("custom_handler"):
            s_out["custom_handler"] = sk["custom_handler"]
        acts = sk.get("actions") or []
        if acts:
            s_out["actions"] = [clean_action_for_export(a) for a in acts]
        skills_out.append(s_out)

    out["skills"] = skills_out
    return out


def format_race_json(race: dict[str, Any]) -> str:
    return json.dumps(clean_race_for_export(race), indent=2, ensure_ascii=False) + "\n"


def validate_race(race: dict[str, Any]) -> tuple[list[str], list[str]]:
    """
    Validate a race definition dict against WCS2's RaceDefinition schema
    and exact registered primitive/handler catalog.
    Returns (errors, warnings).
    """
    errors: list[str] = []
    warnings: list[str] = []

    if not isinstance(race, dict):
        return (["Root JSON must be an object."], [])

    key = str(race.get("key", "")).strip()
    if not key:
        errors.append("Race 'key' is required.")
    elif not re.fullmatch(r"[a-z0-9_]+", key):
        errors.append(f"Race 'key' ({key!r}) must be lowercase snake_case [a-z0-9_]+.")

    name = str(race.get("name", "")).strip()
    if not name:
        errors.append("Race 'name' is required.")

    req_lvl = race.get("required_level", 0)
    if not isinstance(req_lvl, int) or req_lvl < 0:
        errors.append(f"'required_level' must be an integer >= 0 (got {req_lvl!r}).")

    max_lvl = race.get("max_level", 0)
    if not isinstance(max_lvl, int) or max_lvl < 0:
        errors.append(f"'max_level' must be an integer >= 0 (got {max_lvl!r}).")

    restrict_team = race.get("restrict_team", 0)
    if restrict_team not in (0, 2, 3):
        errors.append(f"'restrict_team' must be 0 (any), 2 (T), or 3 (CT) (got {restrict_team!r}).")

    cats = race.get("categories")
    if not isinstance(cats, list) or not cats or not all(isinstance(c, str) and c.strip() for c in cats):
        warnings.append("'categories' should be a non-empty list of strings (e.g. [\"Custom races\", \"All races\"]).")

    race_handler = race.get("custom_handler")
    if race_handler is not None and race_handler != "":
        if not isinstance(race_handler, str) or race_handler.lower() not in CUSTOM_HANDLERS:
            errors.append(f"Unknown race 'custom_handler': {race_handler!r}.")

    def _validate_action_list(actions: Any, ctx_label: str, skill_max_level: int) -> None:
        if actions is None:
            return
        if not isinstance(actions, list):
            errors.append(f"{ctx_label}: 'actions' must be a list.")
            return
        for a_idx, act in enumerate(actions):
            alabel = f"{ctx_label}.actions[{a_idx}]"
            if not isinstance(act, dict):
                errors.append(f"{alabel}: action must be an object.")
                continue
            atype = str(act.get("type", "")).strip()
            if not atype:
                errors.append(f"{alabel}: missing 'type'.")
            elif atype.lower() not in PRIMITIVES:
                errors.append(f"{alabel}: unknown primitive type {atype!r}.")
            target = str(act.get("target", "self")).strip().lower()
            if target and target not in VALID_TARGET_KEYS:
                errors.append(f"{alabel}: invalid target {target!r} (valid: {', '.join(sorted(VALID_TARGET_KEYS))}).")
            delay = act.get("delay", 0)
            if not isinstance(delay, (int, float)) or delay < 0:
                errors.append(f"{alabel}: 'delay' must be a number >= 0.")
            lv = act.get("level_values")
            if lv is not None:
                if not isinstance(lv, dict):
                    errors.append(f"{alabel}: 'level_values' must be an object.")
                else:
                    for lk, arr in lv.items():
                        if not isinstance(arr, list) or not arr:
                            errors.append(f"{alabel}.level_values[{lk!r}]: must be a non-empty list of numbers.")
                        else:
                            if not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in arr):
                                errors.append(f"{alabel}.level_values[{lk!r}]: all entries must be numeric.")
                            if skill_max_level > 0 and len(arr) not in (1, skill_max_level):
                                warnings.append(
                                    f"{alabel}.level_values[{lk!r}]: length {len(arr)} != max_level {skill_max_level} (engine clamps to last element)."
                                )
            params = act.get("params")
            if params is not None and not isinstance(params, dict):
                errors.append(f"{alabel}: 'params' must be an object.")

    for lk in ("spawn_actions", "death_actions", "round_start_actions", "round_end_actions"):
        _validate_action_list(race.get(lk), lk, 1)

    skills = race.get("skills")
    if not isinstance(skills, list) or not skills:
        errors.append("Race must define at least 1 skill in 'skills'.")
    else:
        seen_keys: set[str] = set()
        for s_idx, sk in enumerate(skills):
            slabel = f"skills[{s_idx}]"
            if not isinstance(sk, dict):
                errors.append(f"{slabel}: skill must be an object.")
                continue
            skey = str(sk.get("key", "")).strip()
            if not skey:
                errors.append(f"{slabel}: skill 'key' is required.")
            elif skey.lower() in seen_keys:
                errors.append(f"{slabel}: duplicate skill key {skey!r}.")
            else:
                seen_keys.add(skey.lower())

            sname = str(sk.get("name", "")).strip()
            if not sname:
                errors.append(f"{slabel} ({skey}): skill 'name' is required.")

            trig = str(sk.get("trigger", "")).strip()
            if trig.lower() not in ALL_TRIGGERS_CANONICAL:
                errors.append(f"{slabel} ({skey}): invalid trigger {trig!r}.")

            smax = sk.get("max_level", 0)
            if not isinstance(smax, int) or smax < 1:
                errors.append(f"{slabel} ({skey}): 'max_level' must be an integer >= 1.")
                smax = 1

            sreq = sk.get("required_level", 0)
            if not isinstance(sreq, int) or sreq < 0:
                errors.append(f"{slabel} ({skey}): 'required_level' must be >= 0.")

            cds = sk.get("cooldowns")
            if cds is not None:
                if not isinstance(cds, list) or not all(isinstance(x, (int, float)) and x >= 0 for x in cds):
                    errors.append(f"{slabel} ({skey}): 'cooldowns' must be a list of numbers >= 0.")
                elif cds and len(cds) not in (1, smax):
                    warnings.append(f"{slabel} ({skey}): cooldowns length {len(cds)} != max_level {smax}.")

            chs = sk.get("chances")
            if chs is not None:
                if not isinstance(chs, list) or not all(isinstance(x, (int, float)) and 0 <= x <= 100 for x in chs):
                    errors.append(f"{slabel} ({skey}): 'chances' must be a list of numbers between 0 and 100.")
                elif chs and len(chs) not in (1, smax):
                    warnings.append(f"{slabel} ({skey}): chances length {len(chs)} != max_level {smax}.")

            shandler = sk.get("custom_handler")
            has_handler = False
            if shandler is not None and shandler != "":
                if not isinstance(shandler, str) or shandler.lower() not in CUSTOM_HANDLERS:
                    errors.append(f"{slabel} ({skey}): unknown custom_handler {shandler!r}.")
                else:
                    has_handler = True

            acts = sk.get("actions") or []
            _validate_action_list(acts, f"{slabel}({skey})", smax)
            if not has_handler and not acts:
                warnings.append(f"{slabel} ({skey}): has neither 'custom_handler' nor any 'actions' (skill will do nothing).")

    return errors, warnings


# =============================================================================
# Curses TUI Primitives & Widgets (Crash-Proof & Resize-Safe)
# =============================================================================

class Theme:
    HEADER = 1
    SELECTED = 2
    OK = 3
    ACCENT = 4
    ERR = 5
    MUTED = 6


def init_theme() -> None:
    if not curses.has_colors():
        return
    curses.start_color()
    try:
        curses.use_default_colors()
        bg = -1
    except curses.error:
        bg = curses.COLOR_BLACK

    curses.init_pair(Theme.HEADER, curses.COLOR_CYAN, bg)
    curses.init_pair(Theme.SELECTED, curses.COLOR_BLACK, curses.COLOR_CYAN)
    curses.init_pair(Theme.OK, curses.COLOR_GREEN, bg)
    curses.init_pair(Theme.ACCENT, curses.COLOR_YELLOW, bg)
    curses.init_pair(Theme.ERR, curses.COLOR_RED, bg)
    curses.init_pair(Theme.MUTED, curses.COLOR_WHITE, bg)


def cp(pair_id: int, extra: int = 0) -> int:
    if not curses.has_colors():
        return curses.A_REVERSE if pair_id == Theme.SELECTED else extra
    return curses.color_pair(pair_id) | extra


def safe_addstr(win: curses.window, y: int, x: int, text: str, attr: int = 0) -> None:
    h, w = win.getmaxyx()
    if y < 0 or y >= h or x >= w:
        return
    if x < 0:
        text = text[-x:]
        x = 0
    max_len = w - x
    if y == h - 1:
        max_len -= 1  # Never write the bottom-right cell
    if max_len <= 0:
        return
    clipped = text[:max_len]
    try:
        win.addstr(y, x, clipped, attr)
    except curses.error:
        pass


def safe_hline(win: curses.window, y: int, x: int, length: int, ch: str = "─", attr: int = 0) -> None:
    if length <= 0:
        return
    safe_addstr(win, y, x, ch * length, attr)


def draw_box(win: curses.window, y: int, x: int, h: int, w: int, attr: int = 0, title: str = "") -> None:
    mh, mw = win.getmaxyx()
    if h < 2 or w < 2 or y >= mh or x >= mw:
        return
    h = min(h, mh - y)
    w = min(w, mw - x)
    if h < 2 or w < 2:
        return

    safe_addstr(win, y, x, "┌" + "─" * (w - 2) + "┐", attr)
    for row in range(y + 1, y + h - 1):
        safe_addstr(win, row, x, "│", attr)
        safe_addstr(win, row, x + w - 1, "│", attr)
    safe_addstr(win, y + h - 1, x, "└" + "─" * (w - 2) + "┘", attr)

    if title and w > 6:
        t = f" {title[:w - 6]} "
        safe_addstr(win, y, x + 2, t, attr | curses.A_BOLD)


def wait_if_too_small(stdscr: curses.window) -> bool:
    """If terminal is below MIN_COLS x MIN_ROWS, show a friendly resize screen."""
    h, w = stdscr.getmaxyx()
    if h >= MIN_ROWS and w >= MIN_COLS:
        return False
    stdscr.erase()
    safe_addstr(stdscr, 1, 2, "WCS2 Race Builder", cp(Theme.HEADER, curses.A_BOLD))
    safe_addstr(stdscr, 3, 2, f"Terminal too small ({w}x{h}).", cp(Theme.ERR, curses.A_BOLD))
    safe_addstr(stdscr, 4, 2, f"Please resize to at least {MIN_COLS}x{MIN_ROWS}.", cp(Theme.MUTED))
    safe_addstr(stdscr, 6, 2, "Press any key after resizing, or 'q' to quit.", cp(Theme.ACCENT))
    stdscr.refresh()
    return True


def draw_chrome(
    stdscr: curses.window,
    title: str,
    subtitle: str,
    step_badge: str,
    footer: str,
) -> tuple[int, int, int, int]:
    """
    Draw framed header, main panel, and footer keybar.
    Returns inner content box (top, left, height, width).
    """
    stdscr.erase()
    h, w = stdscr.getmaxyx()

    # Header bar
    safe_addstr(stdscr, 0, 0, " " * (w - 1), cp(Theme.SELECTED))
    left_hdr = f" WCS2 Race Builder │ {title}"
    safe_addstr(stdscr, 0, 0, left_hdr, cp(Theme.SELECTED, curses.A_BOLD))
    if step_badge:
        bx = max(len(left_hdr) + 1, w - len(step_badge) - 2)
        if bx < w - 2:
            safe_addstr(stdscr, 0, bx, f" {step_badge} ", cp(Theme.SELECTED, curses.A_BOLD))

    # Subtitle line
    safe_addstr(stdscr, 1, 2, subtitle, cp(Theme.ACCENT))

    # Main content frame
    box_top = 2
    box_h = max(4, h - 4)
    draw_box(stdscr, box_top, 1, box_h, w - 2, cp(Theme.HEADER))

    # Footer keybar
    safe_addstr(stdscr, h - 1, 0, " " * (w - 1), cp(Theme.HEADER, curses.A_REVERSE))
    safe_addstr(stdscr, h - 1, 1, footer, cp(Theme.HEADER, curses.A_REVERSE))

    return (box_top + 1, 3, max(1, box_h - 2), max(10, w - 6))


@dataclass
class MenuItem:
    key: Any
    label: str
    value: str = ""
    description: str = ""
    accent: int = 0
    is_header: bool = False


def menu_screen(
    stdscr: curses.window,
    title: str,
    subtitle: str,
    step_badge: str,
    items: list[MenuItem],
    footer: str,
    initial_idx: int = 0,
    extra_keys: Optional[dict[int, str]] = None,
) -> tuple[Optional[str], Optional[MenuItem], int]:
    """
    Scrollable menu screen with description footer pane.
    Returns (action, selected_item, index) where action is:
      - "select" (Enter)
      - "back" (Esc / q)
      - mapped string from extra_keys (e.g. "add", "delete", "next", "save")
    """
    selectable = [i for i, it in enumerate(items) if not it.is_header]
    if not selectable:
        return ("back", None, 0)

    idx = initial_idx if initial_idx in selectable else selectable[0]
    scroll = 0
    extra = extra_keys or {}

    while True:
        if wait_if_too_small(stdscr):
            k = stdscr.getch()
            if k in (ord("q"), ord("Q"), 27):
                return ("back", None, idx)
            continue

        top, left, height, width = draw_chrome(stdscr, title, subtitle, step_badge, footer)
        desc_rows = 3 if height >= 9 else 0
        list_h = max(1, height - desc_rows)

        if idx < scroll:
            scroll = idx
        elif idx >= scroll + list_h:
            scroll = idx - list_h + 1
        # Show preceding section header if right above top visible item
        if scroll > 0 and items[scroll - 1].is_header and idx == scroll:
            scroll -= 1

        for row in range(list_h):
            item_i = scroll + row
            if item_i >= len(items):
                break
            it = items[item_i]
            y = top + row
            if it.is_header:
                hdr = f"── {it.label} "
                safe_addstr(stdscr, y, left, hdr + "─" * max(0, width - len(hdr)), cp(Theme.ACCENT, curses.A_BOLD))
                continue

            selected = item_i == idx
            prefix = "▸ " if selected else "  "
            val_str = f" {it.value}" if it.value else ""
            max_lbl = max(8, width - len(prefix) - len(val_str) - 1)
            lbl = it.label[:max_lbl]
            gap = max(1, width - len(prefix) - len(lbl) - len(val_str))
            line = f"{prefix}{lbl}{' ' * gap}{val_str}"

            if selected:
                safe_addstr(stdscr, y, left, line[:width].ljust(width), cp(Theme.SELECTED, curses.A_BOLD))
            else:
                lbl_attr = it.accent if it.accent else cp(Theme.MUTED)
                safe_addstr(stdscr, y, left, prefix + lbl, lbl_attr)
                if val_str:
                    vx = left + width - len(val_str)
                    safe_addstr(stdscr, y, vx, val_str, cp(Theme.HEADER))

        if desc_rows >= 3:
            sep_y = top + list_h
            safe_hline(stdscr, sep_y, left, width, "─", cp(Theme.HEADER))
            cur = items[idx]
            desc = cur.description or "Use ↑/↓ to navigate and Enter to select."
            safe_addstr(stdscr, sep_y + 1, left, desc[:width], cp(Theme.MUTED))
            if len(desc) > width:
                safe_addstr(stdscr, sep_y + 2, left, desc[width : width * 2], cp(Theme.MUTED))

        stdscr.refresh()
        ch = stdscr.getch()

        if ch == curses.KEY_RESIZE:
            continue
        elif ch in (curses.KEY_UP, ord("k")):
            pos = selectable.index(idx)
            idx = selectable[(pos - 1) % len(selectable)]
        elif ch in (curses.KEY_DOWN, ord("j")):
            pos = selectable.index(idx)
            idx = selectable[(pos + 1) % len(selectable)]
        elif ch == curses.KEY_PPAGE:
            pos = max(0, selectable.index(idx) - max(1, list_h - 2))
            idx = selectable[pos]
        elif ch == curses.KEY_NPAGE:
            pos = min(len(selectable) - 1, selectable.index(idx) + max(1, list_h - 2))
            idx = selectable[pos]
        elif ch == curses.KEY_HOME:
            idx = selectable[0]
        elif ch == curses.KEY_END:
            idx = selectable[-1]
        elif ch in (curses.KEY_ENTER, 10, 13):
            return ("select", items[idx], idx)
        elif ch in (27, ord("q"), ord("Q")):
            if ch in extra:
                return (extra[ch], items[idx], idx)
            return ("back", items[idx], idx)
        elif ch in extra:
            return (extra[ch], items[idx], idx)


def text_input_modal(
    stdscr: curses.window,
    title: str,
    prompt: str,
    initial: str = "",
    hint: str = "",
    validator: Optional[Callable[[str], Optional[str]]] = None,
) -> Optional[str]:
    """Framed modal text editor. Returns string on Enter, or None on Esc."""
    buf = list(initial)
    cursor = len(buf)
    err_msg = ""

    try:
        curses.curs_set(1)
    except curses.error:
        pass

    try:
        while True:
            h, w = stdscr.getmaxyx()
            mw = min(max(50, w - 8), 86)
            mh = 10 if hint else 9
            my = max(1, (h - mh) // 2)
            mx = max(1, (w - mw) // 2)

            # Clear modal interior
            for r in range(my, min(h - 1, my + mh)):
                safe_addstr(stdscr, r, mx, " " * min(mw, w - mx - 1), cp(Theme.MUTED))
            draw_box(stdscr, my, mx, mh, mw, cp(Theme.ACCENT), title=title)

            inner_w = mw - 4
            safe_addstr(stdscr, my + 2, mx + 2, prompt[:inner_w], cp(Theme.MUTED, curses.A_BOLD))

            # Input field
            field_y = my + 4
            text_str = "".join(buf)
            view_start = 0
            if cursor >= inner_w - 2:
                view_start = cursor - (inner_w - 3)
            visible = text_str[view_start : view_start + inner_w - 2]
            safe_addstr(stdscr, field_y, mx + 2, " " * inner_w, cp(Theme.SELECTED))
            safe_addstr(stdscr, field_y, mx + 3, visible, cp(Theme.SELECTED, curses.A_BOLD))

            if err_msg:
                safe_addstr(stdscr, my + 6, mx + 2, err_msg[:inner_w], cp(Theme.ERR, curses.A_BOLD))
            elif hint:
                safe_addstr(stdscr, my + 6, mx + 2, hint[:inner_w], cp(Theme.HEADER))

            safe_addstr(
                stdscr,
                my + mh - 2,
                mx + 2,
                "Enter: Confirm  │  Esc: Cancel  │  Ctrl+U: Clear",
                cp(Theme.MUTED),
            )

            cx = min(mx + 3 + (cursor - view_start), mx + mw - 3)
            try:
                stdscr.move(field_y, cx)
            except curses.error:
                pass
            stdscr.refresh()

            ch = stdscr.getch()
            if ch == curses.KEY_RESIZE:
                continue
            elif ch == 27:  # Esc
                return None
            elif ch in (curses.KEY_ENTER, 10, 13):
                val = "".join(buf)
                if validator:
                    err = validator(val)
                    if err:
                        err_msg = err
                        continue
                return val
            elif ch in (curses.KEY_BACKSPACE, 127, 8):
                if cursor > 0:
                    buf.pop(cursor - 1)
                    cursor -= 1
                    err_msg = ""
            elif ch == curses.KEY_DC:
                if cursor < len(buf):
                    buf.pop(cursor)
                    err_msg = ""
            elif ch == curses.KEY_LEFT:
                cursor = max(0, cursor - 1)
            elif ch == curses.KEY_RIGHT:
                cursor = min(len(buf), cursor + 1)
            elif ch == curses.KEY_HOME:
                cursor = 0
            elif ch == curses.KEY_END:
                cursor = len(buf)
            elif ch == 21:  # Ctrl+U
                buf.clear()
                cursor = 0
                err_msg = ""
            elif 32 <= ch <= 126:
                buf.insert(cursor, chr(ch))
                cursor += 1
                err_msg = ""
    finally:
        try:
            curses.curs_set(0)
        except curses.error:
            pass


def confirm_modal(stdscr: curses.window, title: str, message: str, default_yes: bool = False) -> bool:
    choice = default_yes
    while True:
        h, w = stdscr.getmaxyx()
        mw = min(max(46, len(message) + 8), w - 6, 78)
        mh = 8
        my = max(1, (h - mh) // 2)
        mx = max(1, (w - mw) // 2)

        for r in range(my, min(h - 1, my + mh)):
            safe_addstr(stdscr, r, mx, " " * min(mw, w - mx - 1), cp(Theme.MUTED))
        draw_box(stdscr, my, mx, mh, mw, cp(Theme.ACCENT), title=title)
        safe_addstr(stdscr, my + 2, mx + 3, message[: mw - 6], cp(Theme.MUTED, curses.A_BOLD))

        yes_btn = " [ Yes ] "
        no_btn = " [ No ] "
        bx = mx + max(3, (mw - len(yes_btn) - len(no_btn) - 4) // 2)
        safe_addstr(stdscr, my + 4, bx, yes_btn, cp(Theme.SELECTED if choice else Theme.MUTED, curses.A_BOLD))
        safe_addstr(stdscr, my + 4, bx + len(yes_btn) + 4, no_btn, cp(Theme.SELECTED if not choice else Theme.MUTED, curses.A_BOLD))
        safe_addstr(stdscr, my + 6, mx + 3, "←/→ or Y/N: Choose  │  Enter: Confirm  │  Esc: No", cp(Theme.HEADER))
        stdscr.refresh()

        ch = stdscr.getch()
        if ch == curses.KEY_RESIZE:
            continue
        elif ch in (curses.KEY_LEFT, curses.KEY_RIGHT, 9, ord(" ")):
            choice = not choice
        elif ch in (ord("y"), ord("Y")):
            return True
        elif ch in (ord("n"), ord("N"), 27):
            return False
        elif ch in (curses.KEY_ENTER, 10, 13):
            return choice


def message_modal(stdscr: curses.window, title: str, lines: list[str], is_error: bool = False) -> None:
    while True:
        h, w = stdscr.getmaxyx()
        mw = min(max(48, max((len(x) for x in lines), default=20) + 6), w - 6, 84)
        mh = min(len(lines) + 6, h - 4)
        my = max(1, (h - mh) // 2)
        mx = max(1, (w - mw) // 2)

        for r in range(my, min(h - 1, my + mh)):
            safe_addstr(stdscr, r, mx, " " * min(mw, w - mx - 1), cp(Theme.MUTED))
        border_attr = cp(Theme.ERR if is_error else Theme.OK)
        draw_box(stdscr, my, mx, mh, mw, border_attr, title=title)
        for i, ln in enumerate(lines[: mh - 5]):
            safe_addstr(stdscr, my + 2 + i, mx + 3, ln[: mw - 6], cp(Theme.MUTED))
        safe_addstr(stdscr, my + mh - 2, mx + 3, "Press Enter or Esc to continue...", cp(Theme.ACCENT))
        stdscr.refresh()

        ch = stdscr.getch()
        if ch == curses.KEY_RESIZE:
            continue
        if ch in (curses.KEY_ENTER, 10, 13, 27, ord(" "), ord("q"), ord("Q")):
            return


def checkbox_screen(
    stdscr: curses.window,
    title: str,
    subtitle: str,
    step_badge: str,
    options: list[str],
    initial_selected: list[str],
    allow_custom: bool = True,
) -> Optional[list[str]]:
    opts = list(dict.fromkeys(list(options) + list(initial_selected)))
    selected = set(initial_selected)
    idx = 0
    scroll = 0
    footer = "↑/↓: Move │ Space: Toggle │ a: Add Custom │ Enter: Save │ Esc: Cancel" if allow_custom else "↑/↓: Move │ Space: Toggle │ Enter: Save │ Esc: Cancel"

    while True:
        if wait_if_too_small(stdscr):
            if stdscr.getch() in (27, ord("q")):
                return None
            continue

        top, left, height, width = draw_chrome(stdscr, title, subtitle, step_badge, footer)
        list_h = max(1, height - 1)
        if idx < scroll:
            scroll = idx
        elif idx >= scroll + list_h:
            scroll = idx - list_h + 1

        for row in range(list_h):
            i = scroll + row
            if i >= len(opts):
                break
            opt = opts[i]
            mark = "[x]" if opt in selected else "[ ]"
            line = f" {mark} {opt}"
            attr = cp(Theme.SELECTED, curses.A_BOLD) if i == idx else (cp(Theme.OK) if opt in selected else cp(Theme.MUTED))
            safe_addstr(stdscr, top + row, left, line[:width].ljust(width if i == idx else len(line[:width])), attr)

        stdscr.refresh()
        ch = stdscr.getch()
        if ch == curses.KEY_RESIZE:
            continue
        elif ch in (curses.KEY_UP, ord("k")) and opts:
            idx = (idx - 1) % len(opts)
        elif ch in (curses.KEY_DOWN, ord("j")) and opts:
            idx = (idx + 1) % len(opts)
        elif ch == ord(" ") and opts:
            opt = opts[idx]
            if opt in selected:
                selected.remove(opt)
            else:
                selected.add(opt)
        elif ch in (ord("a"), ord("A")) and allow_custom:
            custom = text_input_modal(stdscr, "Add Custom Entry", "Enter new value:", hint="Press Enter to add")
            if custom and custom.strip():
                val = custom.strip()
                if val not in opts:
                    opts.append(val)
                selected.add(val)
                idx = opts.index(val)
        elif ch in (curses.KEY_ENTER, 10, 13):
            return [o for o in opts if o in selected]
        elif ch in (27, ord("q"), ord("Q")):
            return None


def scrollable_text_screen(
    stdscr: curses.window,
    title: str,
    subtitle: str,
    step_badge: str,
    lines: list[tuple[str, int]],
    footer: str,
    extra_keys: Optional[dict[int, str]] = None,
) -> str:
    scroll = 0
    extra = extra_keys or {}

    while True:
        if wait_if_too_small(stdscr):
            if stdscr.getch() in (27, ord("q")):
                return "back"
            continue

        top, left, height, width = draw_chrome(stdscr, title, subtitle, step_badge, footer)
        max_scroll = max(0, len(lines) - height)
        scroll = max(0, min(scroll, max_scroll))

        for row in range(height):
            li = scroll + row
            if li >= len(lines):
                break
            text, attr = lines[li]
            safe_addstr(stdscr, top + row, left, text[:width], attr)

        # Scroll indicator on right border
        if len(lines) > height:
            pct = int(round((scroll / max(1, max_scroll)) * 100))
            ind = f" {scroll + 1}-{min(len(lines), scroll + height)}/{len(lines)} ({pct}%) "
            safe_addstr(stdscr, top - 1, max(3, left + width - len(ind)), ind, cp(Theme.ACCENT))

        stdscr.refresh()
        ch = stdscr.getch()
        if ch == curses.KEY_RESIZE:
            continue
        elif ch in (curses.KEY_UP, ord("k")):
            scroll = max(0, scroll - 1)
        elif ch in (curses.KEY_DOWN, ord("j")):
            scroll = min(max_scroll, scroll + 1)
        elif ch == curses.KEY_PPAGE:
            scroll = max(0, scroll - max(1, height - 2))
        elif ch == curses.KEY_NPAGE:
            scroll = min(max_scroll, scroll + max(1, height - 2))
        elif ch == curses.KEY_HOME:
            scroll = 0
        elif ch == curses.KEY_END:
            scroll = max_scroll
        elif ch in extra:
            return extra[ch]
        elif ch in (27, ord("b"), ord("B"), ord("q"), ord("Q")):
            return "back"


# =============================================================================
# TUI Wizard Screens
# =============================================================================

class RaceBuilderApp:
    def __init__(self, stdscr: curses.window, default_output: Optional[Path] = None, load_path: Optional[Path] = None):
        self.stdscr = stdscr
        self.default_output = default_output
        self.load_path = load_path
        self.race: dict[str, Any] = new_empty_race()
        self.custom_key_edited = False

    def run(self) -> None:
        curses.noecho()
        curses.cbreak()
        self.stdscr.keypad(True)
        try:
            curses.curs_set(0)
        except curses.error:
            pass
        init_theme()

        if self.load_path:
            try:
                self.race = load_race_file(self.load_path)
                self.custom_key_edited = True
                self.run_wizard(is_new=False)
                return
            except Exception as exc:
                message_modal(self.stdscr, "Load Error", [f"Failed to load {self.load_path}:", str(exc)], is_error=True)

        while True:
            items = [
                MenuItem(
                    "new",
                    "Create New Race",
                    "[Wizard]",
                    "Start a guided 3-step wizard to build a new WCS2 race JSON.",
                    accent=cp(Theme.OK, curses.A_BOLD),
                ),
                MenuItem(
                    "load",
                    "Load & Edit Existing Race",
                    f"[{self._count_existing_races()} found]",
                    "Pick an existing race from configs/races/ or enter a custom JSON path.",
                ),
                MenuItem(
                    "browse",
                    "Browse Skill Primitives & Handlers",
                    f"[{len(PRIMITIVES)} + {len(CUSTOM_HANDLERS)}]",
                    f"Inspect all {len(PRIMITIVES)} built-in primitives and {len(CUSTOM_HANDLERS)} reusable custom handlers.",
                ),
                MenuItem("quit", "Quit", "", "Exit WCS2 Race Builder."),
            ]
            action, sel, _ = menu_screen(
                self.stdscr,
                "Main Menu",
                "Build & validate custom WCS2 races — zero C# coding required",
                "Start",
                items,
                "↑/↓: Move │ Enter: Select │ q/Esc: Quit",
            )
            if action == "back" or sel is None or sel.key == "quit":
                return
            if sel.key == "new":
                self.race = new_empty_race()
                self.custom_key_edited = False
                self.run_wizard(is_new=True)
            elif sel.key == "load":
                if self.screen_load_race():
                    self.run_wizard(is_new=False)
            elif sel.key == "browse":
                self.screen_browse_catalog()

    def _count_existing_races(self) -> int:
        if not DEFAULT_RACES_DIR.is_dir():
            return 0
        return len(list(DEFAULT_RACES_DIR.glob("*.json")))

    def screen_load_race(self) -> bool:
        items: list[MenuItem] = []
        if DEFAULT_RACES_DIR.is_dir():
            for p in sorted(DEFAULT_RACES_DIR.glob("*.json")):
                try:
                    r = load_race_file(p)
                    rname = r.get("name", p.stem)
                    scount = len(r.get("skills", []))
                    req = r.get("required_level", 0)
                    items.append(
                        MenuItem(
                            p,
                            f"{rname} ({p.name})",
                            f"Req:{req} │ {scount} skills",
                            str(r.get("description", "")),
                        )
                    )
                except Exception as exc:
                    items.append(MenuItem(p, f"{p.name} [PARSE ERROR]", "", str(exc), accent=cp(Theme.ERR)))

        items.append(
            MenuItem(
                "custom_path",
                "[Custom File Path...]",
                "",
                "Load any race JSON/JSONC file by entering its path.",
                accent=cp(Theme.ACCENT, curses.A_BOLD),
            )
        )

        action, sel, _ = menu_screen(
            self.stdscr,
            "Load Existing Race",
            f"Directory: {DEFAULT_RACES_DIR}",
            "Load",
            items,
            "↑/↓: Move │ Enter: Load │ Esc: Back",
        )
        if action == "back" or sel is None:
            return False

        target_path: Optional[Path]
        if sel.key == "custom_path":
            raw = text_input_modal(self.stdscr, "Load Race Path", "Path to race JSON/JSONC file:", initial=str(DEFAULT_RACES_DIR) + "/")
            if not raw:
                return False
            target_path = Path(raw).expanduser()
        else:
            target_path = sel.key

        try:
            self.race = load_race_file(target_path)
            self.custom_key_edited = True
            return True
        except Exception as exc:
            message_modal(self.stdscr, "Failed to Load Race", [f"File: {target_path}", str(exc)], is_error=True)
            return False

    def screen_browse_catalog(self) -> None:
        lines: list[tuple[str, int]] = []
        lines.append((f"BUILT-IN SKILL PRIMITIVES ({len(PRIMITIVES)} total)", cp(Theme.ACCENT, curses.A_BOLD)))
        lines.append(("", cp(Theme.MUTED)))
        for cat in PRIMITIVE_CATEGORIES:
            lines.append((f"── {cat} ──", cp(Theme.HEADER, curses.A_BOLD)))
            for p in PRIMITIVES_LIST:
                if p.category != cat:
                    continue
                lv_keys = ", ".join(f.name for f in p.level_specs)
                pr_keys = ", ".join(f.name for f in p.param_specs)
                lines.append((f"  {p.key:<22} {p.summary}", cp(Theme.OK)))
                details = []
                if lv_keys:
                    details.append(f"level_values: {lv_keys}")
                if pr_keys:
                    details.append(f"params: {pr_keys}")
                if details:
                    lines.append((f"      {' | '.join(details)}", cp(Theme.MUTED)))
            lines.append(("", cp(Theme.MUTED)))

        lines.append((f"REUSABLE CUSTOM HANDLERS ({len(CUSTOM_HANDLERS)} total)", cp(Theme.ACCENT, curses.A_BOLD)))
        lines.append(("", cp(Theme.MUTED)))
        for h in HANDLERS_LIST:
            lines.append((f"  {h.key:<22} [{h.attach_to}]", cp(Theme.OK)))
            lines.append((f"      {h.summary}", cp(Theme.MUTED)))

        scrollable_text_screen(
            self.stdscr,
            "WCS2 Skill Catalog",
            f"{len(PRIMITIVES)} primitives & {len(CUSTOM_HANDLERS)} custom handlers",
            "Reference",
            lines,
            "↑/↓ PgUp/PgDn: Scroll │ Esc/q: Back",
        )

    def run_wizard(self, is_new: bool) -> None:
        step = 1
        prompted_skill_count = not is_new

        while True:
            if step == 1:
                res = self.step1_metadata()
                if res == "back":
                    return
                step = 2
            elif step == 2:
                if not prompted_skill_count and not self.race["skills"]:
                    prompted_skill_count = True
                    self._prompt_initial_skill_count()
                res = self.step2_skills()
                if res == "back":
                    step = 1
                elif res == "next":
                    step = 3
            elif step == 3:
                res = self.step3_preview_and_save()
                if res == "back":
                    step = 2
                elif res == "done":
                    return

    def _prompt_initial_skill_count(self) -> None:
        def _val_cnt(s: str) -> Optional[str]:
            if not s.strip():
                return None
            if not s.strip().isdigit():
                return "Enter a whole number between 1 and 8."
            n = int(s.strip())
            if not (1 <= n <= 8):
                return "Choose between 1 and 8 skills."
            return None

        raw = text_input_modal(
            self.stdscr,
            "Step 2: Number of Skills",
            "How many skills should this race start with? (1-8):",
            initial="4",
            hint="Default is 4 (3 basic skills + 1 Ultimate). You can add/remove skills anytime.",
            validator=_val_cnt,
        )
        if raw is None:
            return
        count = int(raw.strip()) if raw.strip() else 4
        for i in range(1, count + 1):
            trig = "PlayerUltimate" if (count >= 2 and i == count) else ("PlayerSpawn" if i == 1 else "PlayerAttacker")
            self.race["skills"].append(new_empty_skill(i, trigger=trig))

    # -------------------------------------------------------------------------
    # Step 1 — Race Metadata
    # -------------------------------------------------------------------------
    def step1_metadata(self) -> str:
        idx = 0
        while True:
            r = self.race
            team_label = {0: "0 (Both Teams)", 2: "2 (Terrorists Only)", 3: "3 (CT Only)"}.get(r["restrict_team"], str(r["restrict_team"]))
            wep_summary = "None"
            if r["allowed_weapons"]:
                wep_summary = f"Allowlist ({len(r['allowed_weapons'])})"
            elif r["restricted_weapons"]:
                wep_summary = f"Blocked ({len(r['restricted_weapons'])})"

            lifecycle_cnt = sum(len(r[k]) for k in ("spawn_actions", "death_actions", "round_start_actions", "round_end_actions"))
            rc_handler = r.get("custom_handler") or "None"

            items = [
                MenuItem("name", "Race Name", r["name"], "Display name shown in !changerace and menus."),
                MenuItem("key", "Race Key (filename)", r["key"], "Unique lowercase snake_case identifier & <key>.json filename."),
                MenuItem("author", "Author", r["author"], "Author name displayed in !raceinfo."),
                MenuItem("description", "Description", r["description"][:36], "Short description shown to players."),
                MenuItem("required_level", "Required Total Level", str(r["required_level"]), "Minimum total player level required to unlock this race (0 = unlocked)."),
                MenuItem("max_level", "Max Race Level", "0 (Unlimited)" if r["max_level"] == 0 else str(r["max_level"]), "Cap for this race's level (0 = uncapped, recommended)."),
                MenuItem("team_limit", "Team Limit", "0 (Unlimited)" if r["team_limit"] == 0 else str(r["team_limit"]), "Max players per team allowed to play this race simultaneously."),
                MenuItem("restrict_team", "Restrict Team", team_label, "Restrict race to T (2), CT (3), or Both (0)."),
                MenuItem("categories", "Categories", ", ".join(r["categories"]), "Menu categories for !changerace (multi-select)."),
                MenuItem("weapons", "Weapon Restrictions", wep_summary, "Optional allowed_weapons allowlist or restricted_weapons denylist."),
                MenuItem("lifecycle", "Race Lifecycle & Handler", f"{lifecycle_cnt} act │ {rc_handler}", "Optional spawn/death/round actions or race-level custom_handler."),
                MenuItem(
                    "next",
                    ">>> Continue to Step 2: Skills Setup >>>",
                    f"[{len(r['skills'])} skills]",
                    "Proceed to configure this race's skills, triggers, and actions.",
                    accent=cp(Theme.OK, curses.A_BOLD),
                ),
            ]
            action, sel, idx = menu_screen(
                self.stdscr,
                f"Race Metadata — {r['name']}",
                "Configure race identity, unlock level, categories & weapon rules",
                "Step 1/3",
                items,
                "↑/↓: Move │ Enter: Edit │ n: Next Step │ Esc: Main Menu",
                initial_idx=idx,
                extra_keys={ord("n"): "next", ord("N"): "next"},
            )
            if action == "back" or sel is None:
                return "back"
            if action == "next" or sel.key == "next":
                return "next"

            k = sel.key
            if k == "name":
                val = text_input_modal(self.stdscr, "Race Name", "Enter race display name:", initial=r["name"], validator=lambda s: None if s.strip() else "Name cannot be empty.")
                if val is not None:
                    r["name"] = val.strip()
                    if not self.custom_key_edited:
                        r["key"] = slugify_key(r["name"])
            elif k == "key":
                val = text_input_modal(
                    self.stdscr,
                    "Race Key",
                    "Enter lowercase snake_case race key:",
                    initial=r["key"],
                    hint="Used as DB key and <key>.json filename.",
                    validator=lambda s: None if re.fullmatch(r"[a-z0-9_]+", s.strip()) else "Use only lowercase a-z, 0-9, and _.",
                )
                if val is not None:
                    r["key"] = val.strip()
                    self.custom_key_edited = True
            elif k == "author":
                val = text_input_modal(self.stdscr, "Author", "Enter author name:", initial=r["author"])
                if val is not None:
                    r["author"] = val.strip()
            elif k == "description":
                val = text_input_modal(self.stdscr, "Description", "Enter race description:", initial=r["description"])
                if val is not None:
                    r["description"] = val.strip()
            elif k in ("required_level", "max_level", "team_limit"):
                val = text_input_modal(
                    self.stdscr,
                    k.replace("_", " ").title(),
                    f"Enter integer >= 0 for {k}:",
                    initial=str(r[k]),
                    validator=lambda s: None if s.strip().isdigit() else "Must be an integer >= 0.",
                )
                if val is not None:
                    r[k] = int(val.strip())
            elif k == "restrict_team":
                r["restrict_team"] = {0: 2, 2: 3, 3: 0}.get(r["restrict_team"], 0)
            elif k == "categories":
                chosen = checkbox_screen(
                    self.stdscr,
                    "Race Categories",
                    "Select one or more categories for !changerace",
                    "Step 1/3",
                    STANDARD_CATEGORIES,
                    r["categories"],
                    allow_custom=True,
                )
                if chosen is not None and chosen:
                    r["categories"] = chosen
            elif k == "weapons":
                self._edit_race_weapons()
            elif k == "lifecycle":
                self._edit_race_lifecycle()

    def _edit_race_weapons(self) -> None:
        r = self.race
        while True:
            items = [
                MenuItem("allowed", "Allowed Weapons (Allowlist)", f"{len(r['allowed_weapons'])} weapons", "If non-empty, blocks ALL weapons except these (e.g. knife-only or P250-only)."),
                MenuItem("restricted", "Restricted Weapons (Denylist)", f"{len(r['restricted_weapons'])} weapons", "Specific weapons blocked when Allowlist is empty (e.g. weapon_awp)."),
                MenuItem("clear", "Clear All Weapon Restrictions", "", "Allow all weapons normally.", accent=cp(Theme.ERR)),
                MenuItem("back", "Done", "", "Return to Race Metadata."),
            ]
            action, sel, _ = menu_screen(
                self.stdscr,
                "Weapon Restrictions",
                "Configure race-wide CanAcquire weapon allowlist or denylist",
                "Step 1/3",
                items,
                "↑/↓: Move │ Enter: Select │ Esc: Back",
            )
            if action == "back" or sel is None or sel.key == "back":
                return
            if sel.key == "allowed":
                chosen = checkbox_screen(self.stdscr, "Allowed Weapons", "Only checked weapons will be usable", "Step 1/3", COMMON_WEAPONS, r["allowed_weapons"])
                if chosen is not None:
                    r["allowed_weapons"] = chosen
            elif sel.key == "restricted":
                chosen = checkbox_screen(self.stdscr, "Restricted Weapons", "Checked weapons will be blocked", "Step 1/3", COMMON_WEAPONS, r["restricted_weapons"])
                if chosen is not None:
                    r["restricted_weapons"] = chosen
            elif sel.key == "clear":
                r["allowed_weapons"] = []
                r["restricted_weapons"] = []

    def _edit_race_lifecycle(self) -> None:
        r = self.race
        while True:
            items = [
                MenuItem("spawn_actions", "Spawn Actions", f"{len(r['spawn_actions'])} action(s)", "Run automatically every time the player spawns (e.g. spawn rings, give grenade)."),
                MenuItem("death_actions", "Death Actions", f"{len(r['death_actions'])} action(s)", "Run automatically when the player dies."),
                MenuItem("round_start_actions", "Round Start Actions", f"{len(r['round_start_actions'])} action(s)", "Run at the start of every round."),
                MenuItem("round_end_actions", "Round End Actions", f"{len(r['round_end_actions'])} action(s)", "Run at the end of every round."),
                MenuItem("custom_handler", "Race Custom Handler", r.get("custom_handler") or "None", "Optional race-level C# handler (e.g. shadow_hunter_ward, succubus_skulls, defiladed_destructor)."),
                MenuItem("back", "Done", "", "Return to Race Metadata."),
            ]
            action, sel, _ = menu_screen(
                self.stdscr,
                "Race Lifecycle & Handler",
                "Optional race-wide actions independent of skill level",
                "Step 1/3",
                items,
                "↑/↓: Move │ Enter: Edit │ Esc: Back",
            )
            if action == "back" or sel is None or sel.key == "back":
                return
            if sel.key == "custom_handler":
                picked = self._pick_custom_handler(r.get("custom_handler"))
                if picked != "__CANCEL__":
                    r["custom_handler"] = picked
            else:
                trig_map = {
                    "spawn_actions": "PlayerSpawn",
                    "death_actions": "PlayerDeath",
                    "round_start_actions": "PlayerRoundStart",
                    "round_end_actions": "PlayerRoundEnd",
                }
                self._edit_action_list(r[sel.key], f"Race {sel.label}", trig_map[sel.key], max_level=1)

    # -------------------------------------------------------------------------
    # Step 2 — Skills Setup
    # -------------------------------------------------------------------------
    def step2_skills(self) -> str:
        idx = 0
        while True:
            skills = self.race["skills"]
            items: list[MenuItem] = []
            for i, sk in enumerate(skills):
                trig = sk.get("trigger", "PlayerSpawn")
                ch = sk.get("custom_handler")
                if ch:
                    badge = f"{trig} │ handler:{ch}"
                else:
                    acts = sk.get("actions", [])
                    act_names = ",".join(a.get("type", "?") for a in acts[:2])
                    if len(acts) > 2:
                        act_names += f"+{len(acts) - 2}"
                    badge = f"{trig} │ L{sk.get('max_level', 8)} │ {act_names or 'no actions'}"
                items.append(
                    MenuItem(
                        ("edit", i),
                        f"[{i + 1}] {sk.get('name', sk.get('key', 'Skill'))}",
                        badge,
                        f"key={sk.get('key')} │ {sk.get('description', '')}",
                    )
                )

            items.append(
                MenuItem(
                    ("add", -1),
                    "[+ Add Skill]",
                    "",
                    "Add a new skill to this race.",
                    accent=cp(Theme.ACCENT, curses.A_BOLD),
                )
            )
            items.append(
                MenuItem(
                    ("next", -1),
                    ">>> Continue to Step 3: Live JSON Preview & Save >>>",
                    "",
                    "Inspect the generated JSON, run schema validation, and save.",
                    accent=cp(Theme.OK, curses.A_BOLD),
                )
            )

            action, sel, idx = menu_screen(
                self.stdscr,
                f"Skills Setup — {self.race['name']}",
                f"{len(skills)} skill(s) configured │ Enter to edit, 'a' add, 'd' delete",
                "Step 2/3",
                items,
                "↑/↓: Move │ Enter: Edit │ a: Add │ d: Delete │ n: Preview & Save │ Esc: Back",
                initial_idx=idx,
                extra_keys={
                    ord("a"): "add",
                    ord("A"): "add",
                    ord("d"): "delete",
                    ord("D"): "delete",
                    ord("n"): "next",
                    ord("N"): "next",
                },
            )
            if action == "back" or sel is None:
                return "back"
            if action == "next" or (sel.key[0] == "next"):
                return "next"
            if action == "add" or sel.key[0] == "add":
                sk = new_empty_skill(len(skills) + 1)
                skills.append(sk)
                self._edit_skill(len(skills) - 1)
                idx = len(skills) - 1
            elif action == "delete" and sel.key[0] == "edit":
                del_i = sel.key[1]
                if confirm_modal(self.stdscr, "Delete Skill", f"Delete skill '{skills[del_i]['name']}'?"):
                    skills.pop(del_i)
                    idx = max(0, del_i - 1)
            elif sel.key[0] == "edit":
                self._edit_skill(sel.key[1])

    def _edit_skill(self, skill_index: int) -> None:
        sk = self.race["skills"][skill_index]
        custom_skey = False
        idx = 0

        while True:
            trig = sk.get("trigger", "PlayerSpawn")
            cds_str = ", ".join(str(x) for x in sk.get("cooldowns", [])) if sk.get("cooldowns") else "None"
            chs_str = ", ".join(f"{x}%" for x in sk.get("chances", [])) if sk.get("chances") else "100% (always)"
            weps_str = ", ".join(sk.get("weapons", [])) if sk.get("weapons") else "Any weapon"
            ch_str = sk.get("custom_handler") or "None (use actions)"
            acts = sk.get("actions", [])
            act_summary = f"{len(acts)} action(s): " + ", ".join(a.get("type", "?") for a in acts[:3]) if acts else "0 actions — press Enter to add!"

            items = [
                MenuItem("name", "Skill Name", sk["name"], "Display name shown in !spendskills and !raceinfo."),
                MenuItem("key", "Skill Key", sk["key"], "Unique snake_case key for this skill."),
                MenuItem("description", "Description", sk["description"][:36], "Short description shown to players."),
                MenuItem("trigger", "Trigger Event", trig, dict(RACE_TRIGGERS).get(trig, "Event that activates this skill.")),
                MenuItem("max_level", "Max Skill Level", str(sk["max_level"]), "Maximum points players can invest in this skill (default 8)."),
                MenuItem("required_level", "Required Race Level", str(sk["required_level"]), "Race level needed before investing points (0 for basic, 8 for ultimates)."),
                MenuItem("cooldowns", "Cooldowns (seconds)", cds_str[:30], "Flat value (e.g. 15) or range (e.g. 20..10). Recommended for Ultimates/Abilities."),
                MenuItem("chances", "Proc Chances (%)", chs_str[:30], "Empty = 100%. Flat (10) or range (5..20). Common for PlayerAttacker/Victim/Death."),
                MenuItem("weapons", "Weapon Filter", weps_str[:30], "Optional weapon filter (e.g. weapon_knife or weapon_hegrenade)."),
                MenuItem("custom_handler", "Custom Handler (Optional)", ch_str, "Pick a built-in bespoke C# handler instead of declarative actions."),
                MenuItem(
                    "actions",
                    "Actions Builder",
                    act_summary[:36],
                    "Add/edit composable skill primitives, targets, level_values & params.",
                    accent=cp(Theme.ACCENT if not acts and not sk.get("custom_handler") else Theme.OK, curses.A_BOLD),
                ),
                MenuItem("done", "<<< Done with Skill <<<", "", "Return to Skills List.", accent=cp(Theme.OK, curses.A_BOLD)),
            ]

            action, sel, idx = menu_screen(
                self.stdscr,
                f"Skill #{skill_index + 1} — {sk['name']}",
                f"Trigger: {trig} │ Max Level: {sk['max_level']}",
                "Step 2/3",
                items,
                "↑/↓: Move │ Enter: Edit │ Esc: Back to Skills List",
                initial_idx=idx,
            )
            if action == "back" or sel is None or sel.key == "done":
                return

            k = sel.key
            if k == "name":
                val = text_input_modal(self.stdscr, "Skill Name", "Enter skill name:", initial=sk["name"], validator=lambda s: None if s.strip() else "Name cannot be empty.")
                if val is not None:
                    sk["name"] = val.strip()
                    if not custom_skey:
                        sk["key"] = slugify_key(sk["name"])
            elif k == "key":
                val = text_input_modal(
                    self.stdscr,
                    "Skill Key",
                    "Enter lowercase snake_case skill key:",
                    initial=sk["key"],
                    validator=lambda s: None if re.fullmatch(r"[a-z0-9_]+", s.strip()) else "Use only lowercase a-z, 0-9, and _.",
                )
                if val is not None:
                    sk["key"] = val.strip()
                    custom_skey = True
            elif k == "description":
                val = text_input_modal(self.stdscr, "Skill Description", "Enter skill description:", initial=sk["description"])
                if val is not None:
                    sk["description"] = val.strip()
            elif k == "trigger":
                picked = self._pick_trigger(sk["trigger"])
                if picked:
                    sk["trigger"] = picked
                    if picked == "PlayerUltimate" and sk["required_level"] == 0:
                        sk["required_level"] = 8
            elif k == "max_level":
                val = text_input_modal(
                    self.stdscr,
                    "Max Skill Level",
                    "Enter max level (1-20):",
                    initial=str(sk["max_level"]),
                    validator=lambda s: None if (s.strip().isdigit() and 1 <= int(s.strip()) <= 20) else "Enter integer between 1 and 20.",
                )
                if val is not None:
                    sk["max_level"] = int(val.strip())
            elif k == "required_level":
                val = text_input_modal(
                    self.stdscr,
                    "Required Race Level",
                    "Race level required to level this skill (0+):",
                    initial=str(sk["required_level"]),
                    validator=lambda s: None if s.strip().isdigit() else "Must be an integer >= 0.",
                )
                if val is not None:
                    sk["required_level"] = int(val.strip())
            elif k == "cooldowns":
                cur_txt = ", ".join(str(x) for x in sk.get("cooldowns", []))
                val = text_input_modal(
                    self.stdscr,
                    "Skill Cooldowns (seconds)",
                    f"Flat (15), range (20..10), list, or empty to clear (L1..L{sk['max_level']}):",
                    initial=cur_txt,
                    hint="Examples: '15'  |  '30..15'  |  '6, 5.5, 5, 4.5, 4, 3.5, 3, 2.5'",
                    validator=lambda s: self._validate_num_list_input(s, sk["max_level"], min_v=0.0),
                )
                if val is not None:
                    sk["cooldowns"] = parse_number_list(val, sk["max_level"], as_int=False)
            elif k == "chances":
                cur_txt = ", ".join(str(x) for x in sk.get("chances", []))
                val = text_input_modal(
                    self.stdscr,
                    "Skill Proc Chances (%)",
                    f"Flat (10), range (5..20), list, or empty for 100% (L1..L{sk['max_level']}):",
                    initial=cur_txt,
                    hint="Examples: '10'  |  '5..20'  |  '15, 30, 45, 60, 75, 90'",
                    validator=lambda s: self._validate_num_list_input(s, sk["max_level"], min_v=0.0, max_v=100.0),
                )
                if val is not None:
                    sk["chances"] = parse_number_list(val, sk["max_level"], as_int=False)
            elif k == "weapons":
                chosen = checkbox_screen(
                    self.stdscr,
                    "Weapon Filter",
                    "Only trigger when using checked weapons (leave all unchecked for any weapon)",
                    "Step 2/3",
                    COMMON_WEAPONS,
                    sk.get("weapons", []),
                )
                if chosen is not None:
                    sk["weapons"] = chosen
            elif k == "custom_handler":
                picked = self._pick_custom_handler(sk.get("custom_handler"))
                if picked != "__CANCEL__":
                    sk["custom_handler"] = picked
                    if picked:
                        hspec = CUSTOM_HANDLERS[picked]
                        if hspec.level_specs or hspec.param_specs:
                            if not sk["actions"]:
                                # Seed inert tuning holder action[0] if handler accepts tuning
                                holder = {"type": "message", "target": "self", "delay": 0.0, "level_values": {}, "params": {}}
                                for f in hspec.level_specs:
                                    if f.suggested_start is not None and f.suggested_end is not None:
                                        holder["level_values"][f.name] = interpolate_levels(
                                            f.suggested_start, f.suggested_end, sk["max_level"], as_int=(f.kind == "int")
                                        )
                                for f in hspec.param_specs:
                                    if f.default != "":
                                        holder["params"][f.name] = f.default
                                sk["actions"].append(holder)
            elif k == "actions":
                self._edit_action_list(sk["actions"], f"Actions — {sk['name']}", sk["trigger"], sk["max_level"])

    def _validate_num_list_input(self, raw: str, max_level: int, min_v: Optional[float] = None, max_v: Optional[float] = None) -> Optional[str]:
        if not raw.strip():
            return None
        try:
            vals = parse_number_list(raw, max_level)
        except ValueError:
            return "Invalid number format. Use e.g. '10', '5..20', or '5, 10, 15'."
        for v in vals:
            if min_v is not None and v < min_v:
                return f"Values must be >= {min_v}."
            if max_v is not None and v > max_v:
                return f"Values must be <= {max_v}."
        return None

    def _pick_trigger(self, current: str) -> Optional[str]:
        items = [MenuItem(t, t, "[current]" if t == current else "", desc) for t, desc in RACE_TRIGGERS]
        init_i = next((i for i, (t, _) in enumerate(RACE_TRIGGERS) if t == current), 0)
        action, sel, _ = menu_screen(
            self.stdscr,
            "Select Skill Trigger",
            "Choose which game event activates this skill",
            "Trigger",
            items,
            "↑/↓: Move │ Enter: Select │ Esc: Cancel",
            initial_idx=init_i,
        )
        return sel.key if action == "select" and sel else None

    def _pick_target(self, current: str) -> Optional[str]:
        items = [MenuItem(t, t, "[current]" if t == current else "", desc) for t, desc in TARGETS]
        init_i = next((i for i, (t, _) in enumerate(TARGETS) if t == current), 0)
        action, sel, _ = menu_screen(
            self.stdscr,
            "Select Action Target",
            "Choose who this action affects",
            "Target",
            items,
            "↑/↓: Move │ Enter: Select │ Esc: Cancel",
            initial_idx=init_i,
        )
        return sel.key if action == "select" and sel else None

    def _pick_custom_handler(self, current: Optional[str]) -> Any:
        items: list[MenuItem] = [
            MenuItem(None, "None (use composable primitives in Actions)", "[current]" if not current else "", "Standard declarative skill using the 34 built-in primitives.")
        ]
        init_i = 0
        for i, h in enumerate(HANDLERS_LIST, start=1):
            if h.key == current:
                init_i = i
            items.append(MenuItem(h.key, h.key, f"[{h.attach_to}]", h.summary))

        action, sel, _ = menu_screen(
            self.stdscr,
            "Select Reusable Custom Handler",
            "Optional bespoke C# handler (runs instead of actions[] on a skill)",
            "Handler",
            items,
            "↑/↓: Move │ Enter: Select │ Esc: Cancel",
            initial_idx=init_i,
        )
        if action != "select" or sel is None:
            return "__CANCEL__"
        return sel.key

    def _pick_primitive(self) -> Optional[str]:
        items: list[MenuItem] = []
        for cat in PRIMITIVE_CATEGORIES:
            items.append(MenuItem(None, cat, "", "", is_header=True))
            for p in PRIMITIVES_LIST:
                if p.category != cat:
                    continue
                items.append(MenuItem(p.key, p.key, f"target:{p.default_target}", p.summary))

        action, sel, _ = menu_screen(
            self.stdscr,
            f"Choose Skill Primitive ({len(PRIMITIVES)} Available)",
            "Grouped by category — arrow keys to browse, Enter to pick",
            "Primitive",
            items,
            "↑/↓ PgUp/PgDn: Browse │ Enter: Select │ Esc: Cancel",
        )
        return sel.key if action == "select" and sel else None

    def _edit_action_list(self, actions: list[dict[str, Any]], title: str, trigger: str, max_level: int) -> None:
        idx = 0
        while True:
            items: list[MenuItem] = []
            for i, act in enumerate(actions):
                atype = act.get("type", "?")
                tgt = act.get("target", "self")
                lv_cnt = len(act.get("level_values", {}))
                p_cnt = len(act.get("params", {}))
                d_str = f" +{act['delay']}s" if float(act.get("delay", 0) or 0) > 0 else ""
                items.append(
                    MenuItem(
                        ("edit", i),
                        f"[{i + 1}] {atype} -> {tgt}{d_str}",
                        f"lv:{lv_cnt} params:{p_cnt}",
                        PRIMITIVES[atype].summary if atype in PRIMITIVES else "Custom/unknown primitive",
                    )
                )
            items.append(MenuItem(("add", -1), "[+ Add Primitive Action]", "", "Pick from all 34 built-in WCS2 skill primitives.", accent=cp(Theme.ACCENT, curses.A_BOLD)))
            items.append(MenuItem(("done", -1), "<<< Done with Actions <<<", "", "Return to Skill Editor.", accent=cp(Theme.OK, curses.A_BOLD)))

            action, sel, idx = menu_screen(
                self.stdscr,
                title,
                f"{len(actions)} action(s) │ Enter to edit, 'a' add, 'd' delete",
                "Actions",
                items,
                "↑/↓: Move │ Enter: Edit │ a: Add │ d: Delete │ Esc: Done",
                initial_idx=idx,
                extra_keys={ord("a"): "add", ord("A"): "add", ord("d"): "delete", ord("D"): "delete"},
            )
            if action == "back" or sel is None or sel.key[0] == "done":
                return
            if action == "add" or sel.key[0] == "add":
                prim = self._pick_primitive()
                if prim:
                    actions.append(new_action_for_primitive(prim, trigger=trigger, max_level=max_level))
                    self._edit_action(actions[-1], max_level)
                    idx = len(actions) - 1
            elif action == "delete" and sel.key[0] == "edit":
                del_i = sel.key[1]
                actions.pop(del_i)
                idx = max(0, del_i - 1)
            elif sel.key[0] == "edit":
                self._edit_action(actions[sel.key[1]], max_level)

    def _edit_action(self, act: dict[str, Any], max_level: int) -> None:
        idx = 0
        while True:
            atype = act.get("type", "")
            spec = PRIMITIVES.get(atype)
            lv = act.setdefault("level_values", {})
            params = act.setdefault("params", {})

            lv_preview = ", ".join(f"{k}[{len(v)}]" for k, v in lv.items()) if lv else "None"
            p_preview = ", ".join(f"{k}={v}" for k, v in list(params.items())[:3]) if params else "None"
            if len(params) > 3:
                p_preview += f" (+{len(params) - 3})"

            items = [
                MenuItem("type", "Primitive Type", atype, spec.summary if spec else "Primitive key."),
                MenuItem("target", "Target", act.get("target", "self"), dict(TARGETS).get(act.get("target", "self"), "Who this action targets.")),
                MenuItem("delay", "Delay (seconds)", str(act.get("delay", 0.0)), "Optional execution delay in seconds (0 = immediate)."),
                MenuItem("level_values", "Per-Level Values (level_values)", lv_preview[:36], "Configure values that scale across skill levels (supports Start->End interpolation!).", accent=cp(Theme.ACCENT, curses.A_BOLD)),
                MenuItem("params", "Static Parameters (params)", p_preview[:36], "Configure constant options (mode, colors, sounds, messages, flags)."),
                MenuItem("done", "<<< Done with Action <<<", "", "Return to Actions List.", accent=cp(Theme.OK, curses.A_BOLD)),
            ]

            action, sel, idx = menu_screen(
                self.stdscr,
                f"Action Editor — {atype}",
                spec.summary if spec else "Configure action properties",
                "Action",
                items,
                "↑/↓: Move │ Enter: Edit │ Esc: Done",
                initial_idx=idx,
            )
            if action == "back" or sel is None or sel.key == "done":
                return

            if sel.key == "type":
                picked = self._pick_primitive()
                if picked and picked != atype:
                    act["type"] = picked
                    act["target"] = PRIMITIVES[picked].default_target
            elif sel.key == "target":
                picked = self._pick_target(act.get("target", "self"))
                if picked:
                    act["target"] = picked
            elif sel.key == "delay":
                val = text_input_modal(
                    self.stdscr,
                    "Action Delay",
                    "Delay in seconds (0 for immediate):",
                    initial=str(act.get("delay", 0.0)),
                    validator=lambda s: None if (re.fullmatch(r"\d+(?:\.\d+)?", s.strip())) else "Enter a number >= 0.",
                )
                if val is not None:
                    act["delay"] = float(val.strip())
            elif sel.key == "level_values":
                self._edit_level_values(act, spec, max_level)
            elif sel.key == "params":
                self._edit_params(act, spec)

    def _edit_level_values(self, act: dict[str, Any], spec: Optional[PrimitiveSpec], max_level: int) -> None:
        lv = act.setdefault("level_values", {})
        idx = 0
        while True:
            known_names = [f.name for f in spec.level_specs] if spec else []
            extra_names = [k for k in lv.keys() if k not in known_names]

            items: list[MenuItem] = []
            if spec:
                for f in spec.level_specs:
                    cur = lv.get(f.name)
                    val_txt = str(cur) if cur is not None else f"(default: {f.default}{f.unit})"
                    unit_tag = f" [{f.unit}]" if f.unit else ""
                    items.append(MenuItem(f, f"{f.name}{unit_tag}", val_txt[:36], f.description))
            for k in extra_names:
                items.append(MenuItem(FieldSpec(k, "float", 0.0, "", "Custom level value"), k, str(lv[k])[:36], "Custom level_values key."))

            items.append(MenuItem("add_custom", "[+ Add Custom level_values Key]", "", "Add any numeric per-level key manually.", accent=cp(Theme.ACCENT)))
            items.append(MenuItem("done", "<<< Back to Action <<<", "", "Done editing level_values.", accent=cp(Theme.OK, curses.A_BOLD)))

            action, sel, idx = menu_screen(
                self.stdscr,
                f"Per-Level Values — {act['type']}",
                f"Skill max_level = {max_level} │ Enter to edit, 'd' to clear key",
                "level_values",
                items,
                "↑/↓: Move │ Enter: Edit │ d: Remove Key │ Esc: Back",
                initial_idx=idx,
                extra_keys={ord("d"): "delete", ord("D"): "delete"},
            )
            if action == "back" or sel is None or sel.key == "done":
                return
            if action == "delete" and isinstance(sel.key, FieldSpec):
                lv.pop(sel.key.name, None)
                continue
            if sel.key == "add_custom":
                kname = text_input_modal(self.stdscr, "Custom Key", "Enter level_values key name:", validator=lambda s: None if re.fullmatch(r"[a-z0-9_]+", s.strip()) else "Use snake_case.")
                if kname:
                    fspec = FieldSpec(kname.strip(), "float", 0.0)
                    self._edit_single_level_field(lv, fspec, max_level)
            elif isinstance(sel.key, FieldSpec):
                self._edit_single_level_field(lv, sel.key, max_level)

    def _edit_single_level_field(self, lv: dict[str, Any], fspec: FieldSpec, max_level: int) -> None:
        as_int = fspec.kind == "int"
        cur = lv.get(fspec.name)
        mode_items = [
            MenuItem(
                "range",
                f"Quick Range Fill (Level 1 -> Level {max_level})",
                f"{fspec.suggested_start}..{fspec.suggested_end}" if fspec.suggested_start is not None else "",
                "Enter Start & End values — linearly interpolates all levels automatically!",
                accent=cp(Theme.OK, curses.A_BOLD),
            ),
            MenuItem(
                "list",
                f"Edit Exact List [L1..L{max_level}]",
                str(cur) if cur else "",
                "Type comma-separated numbers (or 'start..end' shorthand).",
            ),
            MenuItem("step", "Edit Level-by-Level", "", "Prompt one value for each level individually."),
            MenuItem("clear", "Remove from level_values", "", "Revert to primitive default or static param.", accent=cp(Theme.ERR)),
        ]
        action, sel, _ = menu_screen(
            self.stdscr,
            f"Edit level_values.{fspec.name}",
            f"{fspec.description} (current: {cur if cur is not None else 'unset'})",
            "Scale",
            mode_items,
            "↑/↓: Move │ Enter: Choose Mode │ Esc: Cancel",
        )
        if action != "select" or sel is None:
            return

        if sel.key == "clear":
            lv.pop(fspec.name, None)
            return

        if sel.key == "range":
            init_s = str(cur[0] if cur else (fspec.suggested_start if fspec.suggested_start is not None else fspec.default))
            init_e = str(cur[-1] if cur else (fspec.suggested_end if fspec.suggested_end is not None else fspec.default))
            s_raw = text_input_modal(
                self.stdscr,
                f"{fspec.name} — Level 1 Value",
                f"Start value at Level 1 ({fspec.unit or 'number'}):",
                initial=init_s,
                validator=lambda s: None if re.fullmatch(r"[+-]?\d+(?:\.\d+)?", s.strip()) else "Enter a valid number.",
            )
            if s_raw is None:
                return
            e_raw = text_input_modal(
                self.stdscr,
                f"{fspec.name} — Level {max_level} Value",
                f"End value at Level {max_level} ({fspec.unit or 'number'}):",
                initial=init_e,
                validator=lambda s: None if re.fullmatch(r"[+-]?\d+(?:\.\d+)?", s.strip()) else "Enter a valid number.",
            )
            if e_raw is None:
                return
            lv[fspec.name] = interpolate_levels(float(s_raw), float(e_raw), max_level, as_int=as_int)
        elif sel.key == "list":
            init_txt = ", ".join(str(x) for x in cur) if cur else ""
            raw = text_input_modal(
                self.stdscr,
                f"{fspec.name} — Exact Values",
                f"Enter comma-separated values or 'start..end' ({max_level} levels):",
                initial=init_txt,
                validator=lambda s: self._validate_num_list_input(s, max_level),
            )
            if raw is not None:
                vals = parse_number_list(raw, max_level, as_int=as_int)
                if vals:
                    lv[fspec.name] = vals
                else:
                    lv.pop(fspec.name, None)
        elif sel.key == "step":
            vals: list[int | float] = []
            for lvl in range(1, max_level + 1):
                prev_val = cur[min(lvl - 1, len(cur) - 1)] if cur else fspec.default
                raw = text_input_modal(
                    self.stdscr,
                    f"{fspec.name} — Level {lvl}/{max_level}",
                    f"Value for Level {lvl}:",
                    initial=str(prev_val),
                    validator=lambda s: None if re.fullmatch(r"[+-]?\d+(?:\.\d+)?", s.strip()) else "Enter a valid number.",
                )
                if raw is None:
                    return
                num = float(raw.strip())
                vals.append(int(round(num)) if as_int else (int(num) if num.is_integer() and "." not in raw else round(num, 4)))
            lv[fspec.name] = vals

    def _edit_params(self, act: dict[str, Any], spec: Optional[PrimitiveSpec]) -> None:
        params = act.setdefault("params", {})
        idx = 0
        while True:
            known = list(spec.param_specs) if spec else []
            known_names = {f.name for f in known}
            items: list[MenuItem] = []

            for f in known:
                if f.name in params:
                    val_str = repr(params[f.name]) if isinstance(params[f.name], (bool, list)) else str(params[f.name])
                else:
                    val_str = f"(default: {f.default})"
                items.append(MenuItem(f, f.name, val_str[:36], f.description))

            for k, v in params.items():
                if k not in known_names:
                    items.append(MenuItem(FieldSpec(k, "str", ""), k, str(v)[:36], "Custom param."))

            items.append(MenuItem("add_custom", "[+ Add Custom Param]", "", "Add any extra static param key/value.", accent=cp(Theme.ACCENT)))
            items.append(MenuItem("done", "<<< Back to Action <<<", "", "Done editing params.", accent=cp(Theme.OK, curses.A_BOLD)))

            action, sel, idx = menu_screen(
                self.stdscr,
                f"Static Params — {act['type']}",
                "Enter to edit/toggle, 'd' to reset to default",
                "params",
                items,
                "↑/↓: Move │ Enter: Edit/Toggle │ d: Remove │ Esc: Back",
                initial_idx=idx,
                extra_keys={ord("d"): "delete", ord("D"): "delete"},
            )
            if action == "back" or sel is None or sel.key == "done":
                return
            if action == "delete" and isinstance(sel.key, FieldSpec):
                params.pop(sel.key.name, None)
                continue
            if sel.key == "add_custom":
                kname = text_input_modal(self.stdscr, "Custom Param Key", "Enter param key name:", validator=lambda s: None if re.fullmatch(r"[a-z0-9_]+", s.strip()) else "Use snake_case.")
                if not kname:
                    continue
                kval = text_input_modal(self.stdscr, f"Value for {kname}", "Enter JSON value or plain string:")
                if kval is None:
                    continue
                params[kname.strip()] = self._parse_loose_json(kval)
                continue

            if isinstance(sel.key, FieldSpec):
                f = sel.key
                cur = params.get(f.name, f.default)
                if f.kind == "bool":
                    params[f.name] = not bool(cur)
                elif f.kind == "choice" and f.choices:
                    c_items = [MenuItem(c, c, "[current]" if c == cur else "", f.description) for c in f.choices]
                    ca, csel, _ = menu_screen(self.stdscr, f"Choose {f.name}", f.description, "Choice", c_items, "↑/↓: Move │ Enter: Select │ Esc: Cancel")
                    if ca == "select" and csel:
                        params[f.name] = csel.key
                elif f.kind == "color":
                    init_c = ", ".join(str(x) for x in cur) if isinstance(cur, list) else "255, 255, 255"
                    raw = text_input_modal(
                        self.stdscr,
                        f"Color RGB — {f.name}",
                        "Enter R, G, B (0-255):",
                        initial=init_c,
                        validator=lambda s: self._validate_rgb(s),
                    )
                    if raw is not None:
                        nums = [max(0, min(255, int(t))) for t in re.split(r"[\s,;]+", raw.strip("[]")) if t]
                        params[f.name] = nums[:3]
                elif f.kind == "str_list":
                    init_l = ", ".join(cur) if isinstance(cur, list) else ""
                    raw = text_input_modal(self.stdscr, f"List — {f.name}", f"{f.description} (comma-separated):", initial=init_l)
                    if raw is not None:
                        vals = [x.strip() for x in raw.split(",") if x.strip()]
                        if vals:
                            params[f.name] = vals
                        else:
                            params.pop(f.name, None)
                elif f.kind == "int":
                    raw = text_input_modal(
                        self.stdscr,
                        f"Integer — {f.name}",
                        f"{f.description}:",
                        initial=str(cur),
                        validator=lambda s: None if re.fullmatch(r"[+-]?\d+", s.strip()) else "Enter an integer.",
                    )
                    if raw is not None:
                        params[f.name] = int(raw.strip())
                elif f.kind == "float":
                    raw = text_input_modal(
                        self.stdscr,
                        f"Number — {f.name}",
                        f"{f.description}:",
                        initial=str(cur),
                        validator=lambda s: None if re.fullmatch(r"[+-]?\d+(?:\.\d+)?", s.strip()) else "Enter a number.",
                    )
                    if raw is not None:
                        params[f.name] = float(raw.strip())
                else:
                    raw = text_input_modal(self.stdscr, f"Text — {f.name}", f"{f.description}:", initial=str(cur))
                    if raw is not None:
                        if raw == "":
                            params.pop(f.name, None)
                        else:
                            params[f.name] = raw

    @staticmethod
    def _validate_rgb(raw: str) -> Optional[str]:
        toks = [t for t in re.split(r"[\s,;]+", raw.strip("[]")) if t]
        if len(toks) != 3 or not all(t.isdigit() and 0 <= int(t) <= 255 for t in toks):
            return "Enter 3 integers 0-255, e.g. 120, 200, 255."
        return None

    @staticmethod
    def _parse_loose_json(raw: str) -> Any:
        s = raw.strip()
        try:
            return json.loads(s)
        except Exception:
            return s

    # -------------------------------------------------------------------------
    # Step 3 — Live JSON Preview, Schema Validation & Save
    # -------------------------------------------------------------------------
    def step3_preview_and_save(self) -> str:
        while True:
            errors, warnings = validate_race(self.race)
            json_text = format_race_json(self.race)

            lines: list[tuple[str, int]] = []
            if errors:
                lines.append((f"✗ VALIDATION FAILED: {len(errors)} error(s), {len(warnings)} warning(s) — press 'v' for details", cp(Theme.ERR, curses.A_BOLD)))
            elif warnings:
                lines.append((f"✓ VALID SCHEMA ({len(warnings)} warning(s) — press 'v' for details)", cp(Theme.ACCENT, curses.A_BOLD)))
            else:
                lines.append(("✓ VALID WCS2 RACE JSON — 0 errors, 0 warnings! Press 's' or Enter to Save.", cp(Theme.OK, curses.A_BOLD)))
            lines.append(("─" * 72, cp(Theme.HEADER)))

            for ln in json_text.splitlines():
                stripped = ln.lstrip()
                if stripped.startswith('"') and '":' in stripped:
                    attr = cp(Theme.HEADER)
                elif stripped in ("{", "}", "[", "]", "},", "],"):
                    attr = cp(Theme.ACCENT)
                else:
                    attr = cp(Theme.MUTED)
                lines.append((ln, attr))

            act = scrollable_text_screen(
                self.stdscr,
                f"Live JSON Preview — {self.race['key']}.json",
                f"Race: {self.race['name']} │ {len(self.race['skills'])} skill(s)",
                "Step 3/3",
                lines,
                "↑/↓ PgUp/PgDn: Scroll │ s/Enter: Save │ v: Validation │ b/Esc: Edit │ q: Main Menu",
                extra_keys={
                    ord("s"): "save",
                    ord("S"): "save",
                    10: "save",
                    13: "save",
                    curses.KEY_ENTER: "save",
                    ord("v"): "validate",
                    ord("V"): "validate",
                    ord("q"): "done",
                    ord("Q"): "done",
                },
            )
            if act == "back":
                return "back"
            if act == "done":
                return "done"
            if act == "validate":
                report: list[str] = []
                if not errors and not warnings:
                    report.append("All checks passed! Zero errors and zero warnings.")
                for e in errors:
                    report.append(f"[ERROR] {e}")
                for w in warnings:
                    report.append(f"[WARN]  {w}")
                message_modal(self.stdscr, "Schema Validation Report", report, is_error=bool(errors))
            elif act == "save":
                if errors:
                    if not confirm_modal(
                        self.stdscr,
                        "Validation Errors",
                        f"Race has {len(errors)} error(s). Save anyway?",
                        default_yes=False,
                    ):
                        continue
                if self._save_race_dialog(json_text):
                    if confirm_modal(self.stdscr, "Saved!", "Return to Main Menu? (No = keep editing)", default_yes=True):
                        return "done"

    def _save_race_dialog(self, json_text: str) -> bool:
        default_path = (
            self.default_output
            if self.default_output is not None
            else (DEFAULT_RACES_DIR / f"{self.race['key']}.json")
        )
        raw = text_input_modal(
            self.stdscr,
            "Save Race JSON",
            "Output file path:",
            initial=str(default_path),
            hint="Drop into plugins/WCS2/configs/races/<key>.json and run `wcs_reload`.",
            validator=lambda s: None if s.strip() else "Path cannot be empty.",
        )
        if not raw:
            return False

        out_path = Path(raw.strip()).expanduser()
        if out_path.exists():
            if not confirm_modal(self.stdscr, "Overwrite File?", f"{out_path.name} already exists. Overwrite?", default_yes=False):
                return False

        try:
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_text(json_text, encoding="utf-8")
            message_modal(
                self.stdscr,
                "Race Saved Successfully",
                [
                    f"Saved to: {out_path}",
                    "",
                    "To activate live on your CS2 server:",
                    "  1. Copy/keep under plugins/WCS2/configs/races/",
                    "  2. Run `wcs_reload` in server console / RCON",
                ],
                is_error=False,
            )
            return True
        except Exception as exc:
            message_modal(self.stdscr, "Save Error", [f"Could not write {out_path}:", str(exc)], is_error=True)
            return False


# =============================================================================
# Non-Interactive CLI Commands (--validate, --list-skills, --self-test)
# =============================================================================

def cmd_list_skills() -> int:
    print(f"WCS2 Built-In Skill Primitives ({len(PRIMITIVES)} total)")
    print("=" * 78)
    for cat in PRIMITIVE_CATEGORIES:
        print(f"\n[{cat}]")
        for p in PRIMITIVES_LIST:
            if p.category != cat:
                continue
            lv = ", ".join(f.name for f in p.level_specs)
            pr = ", ".join(f.name for f in p.param_specs)
            print(f"  {p.key:<22} (target: {p.default_target:<14}) {p.summary}")
            if lv:
                print(f"      level_values: {lv}")
            if pr:
                print(f"      params:       {pr}")

    print(f"\nWCS2 Reusable Custom Handlers ({len(CUSTOM_HANDLERS)} total)")
    print("=" * 78)
    for h in HANDLERS_LIST:
        print(f"  {h.key:<22} [{h.attach_to}]")
        print(f"      {h.summary}")
    return 0


def cmd_validate(paths: list[str]) -> int:
    any_fail = False
    for raw in paths:
        p = Path(raw)
        if not p.is_file():
            print(f"[FAIL] {raw}: file not found")
            any_fail = True
            continue
        try:
            raw_text = p.read_text(encoding="utf-8")
            data = json.loads(strip_jsonc_comments(raw_text))
        except Exception as exc:
            print(f"[FAIL] {p}: JSON/JSONC parse error: {exc}")
            any_fail = True
            continue

        errors, warnings = validate_race(data)
        if errors:
            any_fail = True
            print(f"[FAIL] {p} ({len(errors)} error(s), {len(warnings)} warning(s)):")
            for e in errors:
                print(f"    ERROR: {e}")
            for w in warnings:
                print(f"    WARN:  {w}")
        else:
            scount = len(data.get("skills", []))
            w_note = f" ({len(warnings)} warning(s))" if warnings else ""
            print(f"[OK]   {p} — {data.get('name', '?')} ({data.get('key', '?')}, {scount} skills){w_note}")
            for w in warnings:
                print(f"    WARN:  {w}")
    return 1 if any_fail else 0


def cmd_self_test() -> int:
    # 1. Exact catalog counts matching WCS2 SkillExecutionEngine.cs
    assert len(PRIMITIVES) == 43, f"Expected 43 primitives, got {len(PRIMITIVES)}"
    assert len(CUSTOM_HANDLERS) == 6, f"Expected 6 custom handlers, got {len(CUSTOM_HANDLERS)}"
    assert "teleport" in PRIMITIVES and "teleport_dash" in PRIMITIVES
    for k in ("cheat_death", "splash_damage", "revive_teammate", "player_scale", "infinite_ammo", "instant_restock", "explosive_molotov", "aim_explosion", "air_dash"):
        assert k in PRIMITIVES, f"Missing new primitive: {k}"
    assert "nearby_teammates" in VALID_TARGET_KEYS and "all_teammates" in VALID_TARGET_KEYS
    assert len(RACE_TRIGGERS) == 9

    # 2. Slugify
    assert slugify_key("Frost Assassin!") == "frost_assassin"
    assert slugify_key("  WoW - Death Knight ") == "wow_death_knight"

    # 3. JSONC comment & trailing comma stripping (preserving // inside strings!)
    sample_jsonc = """
    {
      // line comment
      "key": "test_race",
      "url": "https://example.com//keep-me",
      /* block
         comment */
      "nums": [1, 2, 3,],
    }
    """
    parsed = json.loads(strip_jsonc_comments(sample_jsonc))
    assert parsed["url"] == "https://example.com//keep-me"
    assert parsed["nums"] == [1, 2, 3]

    # 4. Linear range interpolation & number list parsing
    assert interpolate_levels(10, 50, 5, as_int=True) == [10, 20, 30, 40, 50]
    assert interpolate_levels(1.10, 1.35, 6) == [1.1, 1.15, 1.2, 1.25, 1.3, 1.35]
    assert parse_number_list("700..1400", 8, as_int=True) == [700, 800, 900, 1000, 1100, 1200, 1300, 1400]
    assert parse_number_list("15", 8) == [15]

    # 5. Round-trip build + validate a complete race
    race = new_empty_race()
    race["key"] = "frost_test"
    race["name"] = "Frost Test"
    sk = new_empty_skill(1, trigger="PlayerUltimate")
    sk["key"] = "frost_dash"
    sk["name"] = "Frost Dash"
    sk["actions"].append(new_action_for_primitive("teleport_dash", trigger="PlayerUltimate", max_level=8))
    race["skills"].append(sk)

    exported = format_race_json(race)
    reloaded = json.loads(exported)
    errs, warns = validate_race(reloaded)
    assert not errs and not warns, f"Unexpected diagnostics: {errs} {warns}"

    # 6. Negative validation test (catches bad trigger, bad primitive, bad handler)
    bad = {
        "key": "Bad Key!",
        "name": "",
        "custom_handler": "nonexistent_handler",
        "skills": [
            {
                "key": "s1",
                "name": "S1",
                "trigger": "NotATrigger",
                "max_level": 4,
                "actions": [{"type": "not_a_primitive", "target": "mars"}],
            }
        ],
    }
    b_errs, _ = validate_race(bad)
    assert len(b_errs) >= 5, f"Expected >=5 validation errors, got {b_errs}"

    # 7. Validate all shipped races if present in repo
    shipped = sorted(DEFAULT_RACES_DIR.glob("*.json")) if DEFAULT_RACES_DIR.is_dir() else []
    for p in shipped:
        data = json.loads(strip_jsonc_comments(p.read_text(encoding="utf-8")))
        e, _ = validate_race(data)
        assert not e, f"Shipped race {p.name} failed validation: {e}"

    print(
        f"[PASS] Self-test succeeded: {len(PRIMITIVES)} primitives, {len(CUSTOM_HANDLERS)} handlers, "
        f"{len(RACE_TRIGGERS)} triggers, JSONC/interpolation/round-trip OK, "
        f"{len(shipped)} shipped races validated."
    )
    return 0


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="wcs2-race-builder.py",
        description="Semi-graphical (curses) TUI & CLI tool to create, edit, and validate WCS2 race JSON files.",
    )
    p.add_argument("--validate", nargs="+", metavar="FILE", help="Validate one or more race JSON/JSONC files and exit.")
    p.add_argument("--list-skills", action="store_true", help=f"Print all {len(PRIMITIVES)} built-in skill primitives and {len(CUSTOM_HANDLERS)} custom handlers.")
    p.add_argument("--self-test", action="store_true", help="Run internal schema, catalog, JSONC, and interpolation unit checks.")
    p.add_argument("--load", metavar="FILE", help="Open an existing race JSON/JSONC file directly in the TUI.")
    p.add_argument("-o", "--output", metavar="FILE", help="Default output path when saving from the TUI.")
    return p


def main(argv: Optional[list[str]] = None) -> int:
    args = build_arg_parser().parse_args(argv)

    if args.self_test:
        return cmd_self_test()
    if args.list_skills:
        return cmd_list_skills()
    if args.validate:
        return cmd_validate(args.validate)

    if not sys.stdin.isatty() or not sys.stdout.isatty():
        print("Error: Interactive TUI requires a TTY terminal. Use --list-skills, --validate, or --self-test for CLI mode.", file=sys.stderr)
        return 2

    os.environ.setdefault("ESCDELAY", "25")
    load_path = Path(args.load).expanduser() if args.load else None
    out_path = Path(args.output).expanduser() if args.output else None

    def _wrapped(stdscr: curses.window) -> None:
        app = RaceBuilderApp(stdscr, default_output=out_path, load_path=load_path)
        app.run()

    try:
        curses.wrapper(_wrapped)
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
