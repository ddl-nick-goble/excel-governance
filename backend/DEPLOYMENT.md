# Domino Deployment Guide

Complete guide for deploying the Domino Governance Tracker backend to Domino Data Lab.

## Prerequisites

1. **Domino Account** with Model API publishing permissions
2. **Domino CLI** installed and configured
3. **Database** (PostgreSQL recommended for production)
4. **Secrets** configured in Domino

## Deployment Steps

### 1. Prepare Database

#### Option A: Use Domino Managed PostgreSQL

If your Domino deployment has managed PostgreSQL:

```bash
# Contact your Domino admin to provision a database
# You'll receive connection details like:
# Host: postgres.domino.svc.cluster.local
# Port: 5432
# Database: dgt_prod
# Username: dgt_user
# Password: <generated>
```

#### Option B: External PostgreSQL

Set up a PostgreSQL database externally:

```sql
-- Connect to PostgreSQL
psql -h your-db-host -U postgres

-- Create database and user
CREATE DATABASE dgt_prod;
CREATE USER dgt_user WITH PASSWORD 'your-secure-password';
GRANT ALL PRIVILEGES ON DATABASE dgt_prod TO dgt_user;

-- Create schema
\c dgt_prod
CREATE SCHEMA IF NOT EXISTS public;
GRANT ALL ON SCHEMA public TO dgt_user;
```

#### Option C: SQLite (Development Only)

For testing/development on Domino:
- Use `sqlite+aiosqlite:///./data/dgt.db`
- Ensure persistent volume is mounted to `/app/data`

### 2. Configure Domino Secrets

Create secrets in Domino for sensitive credentials:

#### Using Domino UI:

1. Navigate to **Settings → Secrets**
2. Create secret: `dgt-database-credentials`
   - Key: `connection_string`
   - Value: `postgresql+asyncpg://dgt_user:password@host:5432/dgt_prod`
3. Create secret: `dgt-api-credentials`
   - Key: `api_key`
   - Value: Generate with `openssl rand -base64 32`

#### Using Domino CLI:

```bash
# Database connection
domino secrets create dgt-database-credentials \
  connection_string="postgresql+asyncpg://dgt_user:PASSWORD@HOST:5432/dgt_prod"

# API key for authentication
domino secrets create dgt-api-credentials \
  api_key="$(openssl rand -base64 32)"
```

### 3. Create Domino Environment

Create a custom environment with required dependencies:

#### Using Dockerfile:

```dockerfile
FROM dominodatalab/python:3.11-slim

# Install dependencies
COPY requirements.txt /tmp/
RUN pip install -r /tmp/requirements.txt

# Install fastapi-proxy for Domino integration
RUN pip install fastapi-proxy
```

#### Using Environment UI:

1. Navigate to **Environments → Create New**
2. Base image: `dominodatalab/python:3.11`
3. Add to **Dockerfile Instructions**:
   ```dockerfile
   RUN pip install fastapi==0.109.2 uvicorn[standard]==0.27.1 \
       sqlalchemy[asyncio]==2.0.25 asyncpg==0.29.0 \
       structlog==24.1.0 apscheduler==3.10.4 \
       tenacity==8.2.3 pydantic-settings==2.1.0 \
       fastapi-proxy
   ```
4. Build environment

### 4. Publish Model API

#### Using Domino UI:

1. **Upload Files:**
   - Upload entire `backend/` folder to Domino project
   - Ensure all files are in project root

2. **Publish Model:**
   - Navigate to **Models → Publish New Model**
   - Name: `domino-governance-backend`
   - Description: `Excel governance tracking backend`
   - Environment: Select the custom environment created above
   - File: `domino_entrypoint.py`
   - Function: Leave blank (FastAPI app is auto-detected)

3. **Configure Model:**
   - **Resources:**
     - CPU: 1-2 cores
     - Memory: 2-4 GB
   - **Replicas:**
     - Min: 1
     - Max: 5 (adjust based on load)
   - **Environment Variables:**
     ```
     APP_NAME=domino-governance-backend
     ENVIRONMENT=production
     LOG_LEVEL=INFO
     CORS_ORIGINS=*
     MAX_BATCH_SIZE=1000
     EVENT_RETENTION_DAYS=90
     ENABLE_BACKGROUND_TASKS=True
     ```
   - **Secrets:**
     - Add `dgt-database-credentials` → `DATABASE_URL`
     - Add `dgt-api-credentials` → `API_KEY`

4. **Health Checks:**
   - Health check path: `/health`
   - Readiness path: `/ready`
   - Liveness path: `/live`

5. **Deploy:**
   - Click "Publish Model"
   - Wait for deployment to complete (2-5 minutes)

#### Using Domino CLI:

```bash
# Navigate to backend directory
cd backend

# Publish model
domino model publish \
  --name "domino-governance-backend" \
  --description "Excel governance tracking backend" \
  --file domino_entrypoint.py \
  --environment-id <YOUR_ENV_ID> \
  --hardware-tier-id <YOUR_HARDWARE_TIER>

# Configure environment variables
domino model env set \
  --model-id <MODEL_ID> \
  APP_NAME=domino-governance-backend \
  ENVIRONMENT=production \
  LOG_LEVEL=INFO

# Add secrets
domino model secrets attach \
  --model-id <MODEL_ID> \
  --secret-id <DATABASE_SECRET_ID> \
  --env-var DATABASE_URL

domino model secrets attach \
  --model-id <MODEL_ID> \
  --secret-id <API_SECRET_ID> \
  --env-var API_KEY
```

### 5. Initialize Database Schema

#### Option 1: Using Alembic (Recommended)

```bash
# Connect to Domino workspace terminal
domino run python -c "
from infrastructure.database import db_manager
from models.database import Base
import asyncio

async def init_db():
    await db_manager.initialize()
    async with db_manager.engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    print('Database initialized')

asyncio.run(init_db())
"
```

#### Option 2: Manual SQL

Connect to PostgreSQL and run:

```sql
-- See models/database.py for full schema
-- This is generated by SQLAlchemy

CREATE TABLE audit_events (
    event_id UUID PRIMARY KEY,
    timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    event_type VARCHAR(50) NOT NULL,
    user_name VARCHAR(255),
    machine_name VARCHAR(255),
    -- ... (see database.py for complete schema)
);

CREATE TABLE sessions (
    session_id VARCHAR(255) PRIMARY KEY,
    user_name VARCHAR(255),
    machine_name VARCHAR(255),
    start_time TIMESTAMP WITH TIME ZONE NOT NULL,
    -- ... (see database.py for complete schema)
);

-- Create indexes (important for performance!)
CREATE INDEX ix_events_timestamp ON audit_events(timestamp);
CREATE INDEX ix_events_timestamp_type ON audit_events(timestamp, event_type);
-- ... (see database.py for all indexes)
```

### 6. Test Deployment

```bash
# Get model URL from Domino
MODEL_URL="https://your-domino.com/models/<model-id>"

# Test health check
curl $MODEL_URL/health

# Test event ingestion
curl -X POST $MODEL_URL/api/events \
  -H "Content-Type: application/json" \
  -d '{
    "events": [{
      "eventId": "550e8400-e29b-41d4-a716-446655440000",
      "timestamp": "2025-01-15T10:30:00Z",
      "eventType": "CellChange",
      "userName": "test.user",
      "sessionId": "test_session",
      "workbookName": "Test.xlsx",
      "cellAddress": "$A$1",
      "newValue": "100"
    }]
  }'

# Test query
curl -X POST $MODEL_URL/api/events/query \
  -H "Content-Type: application/json" \
  -d '{
    "offset": 0,
    "limit": 10,
    "order_by": "timestamp",
    "order_desc": true
  }'
```

### 7. Update Excel Add-in Configuration

Update the Excel add-in config to point to your Domino deployment:

**config.json:**
```json
{
  "apiEndpoint": "https://your-domino.com/models/<model-id>/api/events",
  "apiKey": "your-api-key-from-secrets",
  "trackingEnabled": true,
  "maxBufferSize": 100,
  "flushIntervalSeconds": 10,
  "maxRetryAttempts": 3,
  "httpTimeoutSeconds": 30
}
```

Distribute updated config with the Excel add-in.

## Monitoring & Maintenance

### View Logs

```bash
# Using Domino UI
# Navigate to Model → Logs → Select instance

# Using CLI
domino model logs --model-id <MODEL_ID> --follow
```

### Monitor Health

Set up monitoring for:
- `/health` endpoint (every 30s)
- Alert on `status: "unhealthy"`
- Alert on high latency (>1s for health checks)

### Database Maintenance

```sql
-- Check table sizes
SELECT
    schemaname,
    tablename,
    pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size
FROM pg_tables
WHERE schemaname = 'public'
ORDER BY pg_total_relation_size(schemaname||'.'||tablename) DESC;

-- Check event count
SELECT COUNT(*) FROM audit_events;

-- Check oldest/newest events
SELECT MIN(timestamp), MAX(timestamp) FROM audit_events;

-- Vacuum and analyze
VACUUM ANALYZE audit_events;
VACUUM ANALYZE sessions;
```

### Scaling

#### Horizontal Scaling (More Replicas):
1. Navigate to Model → Settings
2. Increase Max Replicas
3. Adjust auto-scaling thresholds

#### Vertical Scaling (More Resources):
1. Create new hardware tier with more CPU/memory
2. Restart model with new tier

#### Database Scaling:
- **Connection Pool:** Increase `DB_POOL_SIZE` and `DB_MAX_OVERFLOW`
- **Read Replicas:** Point queries to read replica
- **Partitioning:** Partition `audit_events` by timestamp for large datasets

### Backup & Recovery

#### Database Backups:

```bash
# Automated backup (cron)
pg_dump -h HOST -U dgt_user dgt_prod | gzip > backup_$(date +%Y%m%d).sql.gz

# Restore from backup
gunzip -c backup_20250115.sql.gz | psql -h HOST -U dgt_user dgt_prod
```

#### Export Events to S3/Azure:

```python
# Add to background_service.py
async def _export_old_events():
    """Export old events to cloud storage before deletion."""
    # Query events older than retention period
    # Export to parquet/json
    # Upload to S3/Azure
    # Delete from database
```

## Troubleshooting

### Model won't start

**Check logs for:**
- Import errors → Missing dependencies
- Database connection errors → Check `DATABASE_URL` secret
- Permission errors → Check secrets are attached

**Solutions:**
```bash
# Rebuild environment with all dependencies
# Verify secrets:
domino secrets list | grep dgt

# Test database connection separately
```

### Slow performance

**Diagnosis:**
```sql
-- Find slow queries
SELECT query, calls, total_time, mean_time
FROM pg_stat_statements
ORDER BY mean_time DESC
LIMIT 10;

-- Check missing indexes
SELECT schemaname, tablename, attname
FROM pg_stats
WHERE schemaname = 'public'
  AND n_distinct > 100
  AND correlation < 0.1;
```

**Solutions:**
- Add missing indexes
- Increase connection pool
- Scale up hardware tier
- Enable query caching

### High memory usage

**Check:**
- Connection pool size (reduce if too high)
- Batch size (reduce `MAX_BATCH_SIZE`)
- Background tasks (disable if not needed)

**Monitor:**
```python
import psutil
process = psutil.Process()
print(f"Memory: {process.memory_info().rss / 1024 / 1024:.2f} MB")
```

### Database connection pool exhausted

**Symptoms:**
- "QueuePool limit reached" errors
- Timeouts on API requests

**Solutions:**
```env
# Increase pool size
DB_POOL_SIZE=50
DB_MAX_OVERFLOW=100

# Reduce pool timeout (fail faster)
DB_POOL_TIMEOUT=10
```

## Production Checklist

- [ ] Database credentials stored in Domino secrets
- [ ] API key generated and stored securely
- [ ] Database schema initialized
- [ ] Health checks configured
- [ ] Auto-scaling enabled
- [ ] Monitoring/alerting set up
- [ ] Backup strategy implemented
- [ ] Logs retention configured
- [ ] Excel add-in config updated
- [ ] Load testing completed
- [ ] Disaster recovery plan documented

## Support

For deployment issues:
1. Check Domino model logs
2. Verify database connectivity
3. Test health endpoints
4. Review environment variables
5. Contact Domino support if needed

## Additional Resources

- [Domino Model APIs Documentation](https://docs.dominodatalab.com/en/latest/user_guide/model_apis/)
- [FastAPI on Domino](https://github.com/hiiamelliott/domino-fastapi)
- [PostgreSQL Performance Tuning](https://wiki.postgresql.org/wiki/Performance_Optimization)
