# Stop all running containers
docker stop $(docker ps -aq)

# Remove all containers
docker rm $(docker ps -aq)

# Remove all images
docker rmi $(docker images -q) --force

# Remove all volumes
docker volume rm $(docker volume ls -q)

# Remove all networks (except default ones)
docker network rm $(docker network ls -q -f "type=custom")

# Remove any dangling resources (cache, build cache, etc.)
docker system prune -a --volumes

# docker-compose down
# docker-compose build sqlflow-mssql
# docker-compose up -d

# docker compose  -f "B:\Github\SQLFlow\docker-compose.yml" -f "B:\Github\SQLFlow\docker-compose.override.yml" -p dockercompose1024744025637796868 --ansi never up -d --build --remove-orphans