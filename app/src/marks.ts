import { Severity, Verdict } from "./types";

/** The mark in front of a result: problems first, then the reference verdict. */
export function markOf(problem: Severity | null, reference: { verdict: Verdict } | undefined, hasValue: boolean): { mark: string; kind: string } {
  if (problem === "error") {
    return { mark: "✗", kind: "error" };
  }
  if (reference?.verdict === "mismatch") {
    return { mark: "≠", kind: "error" };
  }
  if (problem === "warning") {
    return { mark: "⚠", kind: "warning" };
  }
  switch (reference?.verdict) {
    case "match":
      return { mark: "✓", kind: "match" };
    case "withinTolerance":
      return { mark: "≈", kind: "approximate" };
    case "warpLimit":
      return { mark: "⊘", kind: "warning" };
    case "notChecked":
      return { mark: "–", kind: "dim" };
  }
  return hasValue ? { mark: "…", kind: "dim" } : { mark: "", kind: "dim" };
}

/** The worst of several verdicts (a line made of several values); undefined when none is known. */
export function worstVerdict(verdicts: (Verdict | undefined)[]): Verdict | undefined {
  const rank = (verdict: Verdict): number =>
    ({ match: 0, notChecked: 0, withinTolerance: 1, warpLimit: 2, mismatch: 3 })[verdict];
  let worst: Verdict | undefined;
  for (const verdict of verdicts) {
    if (verdict !== undefined && (worst === undefined || rank(verdict) > rank(worst))) {
      worst = verdict;
    }
  }
  return worst;
}
