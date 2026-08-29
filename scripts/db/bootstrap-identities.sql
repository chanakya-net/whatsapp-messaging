-- MessageBridge database identity bootstrap.
-- Required session settings:
--   messagebridge.runtime_role
--   messagebridge.migrator_role
--   messagebridge.bootstrap_mode = apply | verify
-- Optional internal setting used only on the Azure postgres database:
--   messagebridge.principals_only = on

DO $bootstrap$
DECLARE
    runtime_role text := current_setting('messagebridge.runtime_role', true);
    migrator_role text := current_setting('messagebridge.migrator_role', true);
    bootstrap_mode text := current_setting('messagebridge.bootstrap_mode', true);
    principals_only boolean :=
        COALESCE(current_setting('messagebridge.principals_only', true), 'off') = 'on';
    role_name text;
    database_name text := current_database();
    object_record record;
BEGIN
    IF runtime_role IS NULL OR runtime_role = '' OR migrator_role IS NULL OR migrator_role = '' THEN
        RAISE EXCEPTION 'runtime and migrator role settings are required';
    END IF;
    IF runtime_role = migrator_role THEN
        RAISE EXCEPTION 'runtime and migrator roles must differ';
    END IF;
    IF bootstrap_mode NOT IN ('apply', 'verify') THEN
        RAISE EXCEPTION 'bootstrap mode must be apply or verify';
    END IF;

    IF bootstrap_mode = 'apply' THEN
        FOREACH role_name IN ARRAY ARRAY[runtime_role, migrator_role]
        LOOP
            IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = role_name) THEN
                IF to_regprocedure('pg_catalog.pgaadauth_create_principal(text,boolean,boolean)') IS NOT NULL THEN
                    EXECUTE format(
                        'SELECT pg_catalog.pgaadauth_create_principal(%L, false, false)',
                        role_name);
                ELSE
                    EXECUTE format('CREATE ROLE %I LOGIN', role_name);
                END IF;
            END IF;
            EXECUTE format(
                'ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
                role_name);
        END LOOP;

        IF principals_only THEN
            RETURN;
        END IF;

        EXECUTE format('GRANT %I TO %I', migrator_role, current_user);
        EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', database_name);
        EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', database_name);
        EXECUTE format('REVOKE ALL PRIVILEGES ON DATABASE %I FROM %I', database_name, runtime_role);
        EXECUTE format('REVOKE ALL PRIVILEGES ON DATABASE %I FROM %I', database_name, migrator_role);
        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I, %I', database_name, runtime_role, migrator_role);

        REVOKE CREATE ON SCHEMA public FROM PUBLIC;
        EXECUTE format('REVOKE ALL PRIVILEGES ON SCHEMA public FROM %I', runtime_role);
        EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', runtime_role);
        EXECUTE format('ALTER SCHEMA public OWNER TO %I', migrator_role);
        EXECUTE format('GRANT USAGE, CREATE ON SCHEMA public TO %I', migrator_role);

        FOR object_record IN
            SELECT namespace.nspname AS schema_name,
                   relation.relname AS object_name,
                   relation.relkind
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relkind IN ('r', 'p', 'S')
        LOOP
            IF object_record.relkind = 'S' THEN
                EXECUTE format(
                    'ALTER SEQUENCE %I.%I OWNER TO %I',
                    object_record.schema_name,
                    object_record.object_name,
                    migrator_role);
            ELSE
                EXECUTE format(
                    'ALTER TABLE %I.%I OWNER TO %I',
                    object_record.schema_name,
                    object_record.object_name,
                    migrator_role);
            END IF;
        END LOOP;

        EXECUTE format('REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM %I', runtime_role);
        EXECUTE format(
            'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I',
            runtime_role);
        EXECUTE format('REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM %I', runtime_role);
        EXECUTE format(
            'GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO %I',
            runtime_role);
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public '
            'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
            migrator_role,
            runtime_role);
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public '
            'GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I',
            migrator_role,
            runtime_role);
    END IF;

    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = runtime_role)
       OR NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = migrator_role) THEN
        RAISE EXCEPTION 'MessageBridge roles are missing';
    END IF;
    IF NOT has_database_privilege(runtime_role, database_name, 'CONNECT')
       OR NOT has_database_privilege(migrator_role, database_name, 'CONNECT') THEN
        RAISE EXCEPTION 'MessageBridge roles lack CONNECT on %', database_name;
    END IF;
    IF has_database_privilege(runtime_role, database_name, 'TEMPORARY') THEN
        RAISE EXCEPTION 'runtime role can create temporary objects';
    END IF;
    IF EXISTS (
        SELECT
        FROM pg_catalog.pg_database AS target_database
        CROSS JOIN LATERAL aclexplode(
            COALESCE(target_database.datacl, acldefault('d', target_database.datdba))) AS privilege
        WHERE target_database.datname = database_name
          AND privilege.grantee = 0
          AND privilege.privilege_type = 'CONNECT') THEN
        RAISE EXCEPTION 'PUBLIC can connect to %', database_name;
    END IF;
    IF EXISTS (
        SELECT
        FROM pg_catalog.pg_roles
        WHERE rolname IN (runtime_role, migrator_role)
          AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
        RAISE EXCEPTION 'MessageBridge roles have forbidden cluster privileges';
    END IF;
    IF has_schema_privilege(runtime_role, 'public', 'CREATE')
       OR NOT has_schema_privilege(runtime_role, 'public', 'USAGE') THEN
        RAISE EXCEPTION 'runtime schema privileges violate least privilege';
    END IF;
    IF NOT has_schema_privilege(migrator_role, 'public', 'USAGE')
       OR NOT has_schema_privilege(migrator_role, 'public', 'CREATE') THEN
        RAISE EXCEPTION 'migrator lacks schema DDL privileges';
    END IF;
    IF (SELECT pg_get_userbyid(nspowner) FROM pg_catalog.pg_namespace WHERE nspname = 'public')
       <> migrator_role THEN
        RAISE EXCEPTION 'migrator does not own the public schema';
    END IF;
    IF EXISTS (
        SELECT
        FROM pg_catalog.pg_class AS relation
        JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.nspname = 'public'
          AND relation.relkind IN ('r', 'p')
          AND (
              NOT has_table_privilege(runtime_role, relation.oid, 'SELECT')
              OR NOT has_table_privilege(runtime_role, relation.oid, 'INSERT')
              OR NOT has_table_privilege(runtime_role, relation.oid, 'UPDATE')
              OR NOT has_table_privilege(runtime_role, relation.oid, 'DELETE')
              OR has_table_privilege(runtime_role, relation.oid, 'TRUNCATE,REFERENCES,TRIGGER')
              OR pg_get_userbyid(relation.relowner) <> migrator_role)) THEN
        RAISE EXCEPTION 'table grants or ownership violate the MessageBridge contract';
    END IF;
    IF EXISTS (
        SELECT
        FROM pg_catalog.pg_class AS relation
        JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.nspname = 'public'
          AND relation.relkind = 'S'
          AND (
              NOT has_sequence_privilege(runtime_role, relation.oid, 'USAGE')
              OR NOT has_sequence_privilege(runtime_role, relation.oid, 'SELECT')
              OR NOT has_sequence_privilege(runtime_role, relation.oid, 'UPDATE')
              OR pg_get_userbyid(relation.relowner) <> migrator_role)) THEN
        RAISE EXCEPTION 'sequence grants or ownership violate the MessageBridge contract';
    END IF;
    IF EXISTS (
        SELECT required.privilege
        FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE']) AS required(privilege)
        WHERE NOT EXISTS (
            SELECT
            FROM pg_catalog.pg_default_acl AS defaults
            CROSS JOIN LATERAL aclexplode(defaults.defaclacl) AS privilege
            WHERE defaults.defaclrole = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = migrator_role)
              AND defaults.defaclnamespace = 'public'::regnamespace
              AND defaults.defaclobjtype = 'r'
              AND privilege.grantee = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = runtime_role)
              AND privilege.privilege_type = required.privilege)) THEN
        RAISE EXCEPTION 'runtime table default privileges are incomplete';
    END IF;
    IF EXISTS (
        SELECT required.privilege
        FROM unnest(ARRAY['USAGE', 'SELECT', 'UPDATE']) AS required(privilege)
        WHERE NOT EXISTS (
            SELECT
            FROM pg_catalog.pg_default_acl AS defaults
            CROSS JOIN LATERAL aclexplode(defaults.defaclacl) AS privilege
            WHERE defaults.defaclrole = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = migrator_role)
              AND defaults.defaclnamespace = 'public'::regnamespace
              AND defaults.defaclobjtype = 'S'
              AND privilege.grantee = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = runtime_role)
              AND privilege.privilege_type = required.privilege)) THEN
        RAISE EXCEPTION 'runtime sequence default privileges are incomplete';
    END IF;
END
$bootstrap$;
