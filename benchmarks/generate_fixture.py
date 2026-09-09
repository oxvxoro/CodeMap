#!/usr/bin/env python3
"""Create a deterministic CodeMap benchmark corpus."""

from __future__ import annotations

import argparse
from pathlib import Path


def generate(root: Path, projects: int, symbols: int) -> None:
    if projects <= 0 or symbols <= 0 or symbols < projects:
        raise ValueError("projects and symbols must be positive, with symbols >= projects")
    root.mkdir(parents=True, exist_ok=True)
    per_project = symbols // projects
    remainder = symbols % projects
    for index in range(projects):
        name = f"Generated{index:03d}"
        project_dir = root / name
        project_dir.mkdir(parents=True, exist_ok=True)
        project_dir.joinpath(f"{name}.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n'
            '</Project>\n', encoding="utf-8")
        count = per_project + (1 if index < remainder else 0)
        lines = [f"namespace {name};", f"public static class Symbols{index:03d}", "{"]
        for symbol in range(count):
            lines.append(f"    public static int Symbol{symbol:05d}(int value) => value + {symbol};")
        lines.append("}")
        project_dir.joinpath("Symbols.cs").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--projects", type=int, default=50)
    parser.add_argument("--symbols", type=int, default=50_000)
    args = parser.parse_args()
    generate(args.output, args.projects, args.symbols)


if __name__ == "__main__":
    main()
