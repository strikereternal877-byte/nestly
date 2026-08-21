/**
 * Direct Postgres access for the load harness (task #387).
 *
 * The harness drives the platform through real HTTP everywhere it can. This
 * module exists for the one thing HTTP cannot do: read the *resulting
 * database state* after a race, which is the only place an overbooked slot is
 * actually visible. Response codes alone cannot prove the invariant - a
 * capacity bug that returns 201 to two racers looks identical, over HTTP, to
 * a correct run where the second racer legitimately got the second-to-last
 * seat.
 *
 * `psql` is not assumed to be on PATH (it is not, on the machine this
 * baseline was recorded on). By default the harness shells into the running
 * Postgres container, matching how docker-compose.yml stands the database up.
 * Set NESTLY_PSQL to a real psql invocation to bypass that (e.g. in CI, where
 * Postgres is a service container reachable directly).
 */
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

const PG_CONTAINER = process.env.NESTLY_PG_CONTAINER ?? "nestly-postgres-1";
const PG_USER = process.env.NESTLY_PG_USER ?? "nestly";
const PG_DATABASE = process.env.NESTLY_PG_DATABASE ?? "nestly";

/** Builds the argv that runs `psql` with the given trailing arguments. */
function psqlArgv(args) {
  if (process.env.NESTLY_PSQL) {
    // e.g. NESTLY_PSQL='psql postgresql://nestly:nestly_dev@localhost:5432/nestly'
    const parts = process.env.NESTLY_PSQL.split(/\s+/).filter(Boolean);
    return [parts[0], [...parts.slice(1), ...args]];
  }
  return [
    "docker",
    ["exec", "-i", PG_CONTAINER, "psql", "-U", PG_USER, "-d", PG_DATABASE, ...args],
  ];
}

/**
 * Runs a query and returns rows as arrays of column strings.
 * `-A -t -F '\t'` gives unaligned, header-less, tab-separated output, which is
 * the least ambiguous thing psql can emit without a JSON round trip.
 */
export async function query(sql) {
  const [cmd, args] = psqlArgv(["-v", "ON_ERROR_STOP=1", "-A", "-t", "-F", "\t", "-c", sql]);
  const { stdout } = await execFileAsync(cmd, args, { maxBuffer: 32 * 1024 * 1024 });
  return stdout
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => line.split("\t"));
}

/** Runs a query expected to yield exactly one row of one column. */
export async function scalar(sql) {
  const rows = await query(sql);
  if (rows.length !== 1) {
    throw new Error(`Expected exactly 1 row from: ${sql}\nGot ${rows.length}`);
  }
  return rows[0][0];
}

/**
 * Executes a script read from stdin (used for the load-customer bootstrap).
 * `vars` become psql `-v name=value` variables, referenced in the script as
 * `:name`.
 */
export async function runScript(sql, vars = {}) {
  const varArgs = Object.entries(vars).flatMap(([k, v]) => ["-v", `${k}=${v}`]);
  const [cmd, args] = psqlArgv(["-v", "ON_ERROR_STOP=1", ...varArgs, "-q", "-f", "-"]);
  const child = execFileAsync(cmd, args, { maxBuffer: 32 * 1024 * 1024 });
  child.child.stdin.end(sql);
  const { stdout } = await child;
  return stdout;
}

/** Escapes a string for inlining into a SQL literal. */
export function lit(value) {
  return `'${String(value).replace(/'/g, "''")}'`;
}
