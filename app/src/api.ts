import { invoke } from "@tauri-apps/api/core";

/** Whether the page runs inside the Tauri app (otherwise: a browser talking to the dev server). */
export const isTauri: boolean = "__TAURI_INTERNALS__" in window;

/** Runs a backend command: Tauri IPC in the app, `POST /api/<command>` in a browser. */
export async function call<T>(command: string, args: object = {}): Promise<T> {
  if (isTauri) {
    return invoke<T>("run_command", { command, args });
  }
  const response: Response = await fetch(`/api/${command}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
  const body: unknown = await response.json();
  if (!response.ok) {
    throw new Error(typeof body === "string" ? body : JSON.stringify(body));
  }
  return body as T;
}
