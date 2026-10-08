-- HP-84 operator-only preflight. Run against the intended pre-HP84 database.
-- PostgreSQL enforces read-only mode; only aggregate counts leave the database.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
WITH roster AS (
    SELECT p.id AS player_id, l.event_id, p.user_id, p.event_guest_id,
           g.event_id AS guest_event_id, e.id AS existing_event_id
    FROM players p
    LEFT JOIN lines l ON l.id = p.line_id
    LEFT JOIN events e ON e.id = l.event_id
    LEFT JOIN event_guests g ON g.id = p.event_guest_id
), duplicate_users AS (
    SELECT COUNT(*) AS roster_rows FROM roster WHERE user_id IS NOT NULL
    GROUP BY event_id, user_id HAVING COUNT(*) > 1
), duplicate_guests AS (
    SELECT COUNT(*) AS roster_rows FROM roster WHERE event_guest_id IS NOT NULL
    GROUP BY event_id, event_guest_id HAVING COUNT(*) > 1
), counts AS (
    SELECT
        (SELECT COUNT(*) FROM duplicate_users) AS duplicate_user_groups,
        (SELECT COALESCE(SUM(roster_rows), 0)::bigint FROM duplicate_users) AS duplicate_user_roster_rows,
        (SELECT COUNT(*) FROM duplicate_guests) AS duplicate_guest_groups,
        (SELECT COALESCE(SUM(roster_rows), 0)::bigint FROM duplicate_guests) AS duplicate_guest_roster_rows,
        (SELECT COUNT(*) FROM roster WHERE existing_event_id IS NULL) AS players_missing_line_or_event,
        (SELECT COUNT(*) FROM roster WHERE event_guest_id IS NOT NULL
            AND guest_event_id IS DISTINCT FROM event_id) AS players_foreign_or_missing_guest
), report AS (
    SELECT *, duplicate_user_groups + duplicate_guest_groups + players_missing_line_or_event
        + players_foreign_or_missing_guest AS anomaly_count FROM counts
)
SELECT 1 AS schema_version, *, CASE WHEN anomaly_count = 0 THEN 'PASS' ELSE 'BLOCKED' END AS status
FROM report;
ROLLBACK;
