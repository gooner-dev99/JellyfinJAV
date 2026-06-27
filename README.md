# JellyfinJav
Don't expect perfection.

# Metadata Providers
* R18.dev (Videos)
* AsianScreens (Actresses)
* Warashi Asian Pornstars (Actresses)

# Instructions
### Installation

Add the Repo url to Jellyfin using `https://raw.githubusercontent.com/gooner-dev99/JellyfinJAV/master/manifest.json`


### Usage
When adding the media library, make sure to select "Content type: movies".

### Example File Names
* abp200.mkv
* ABP200.mkv
* ABP-200.mkv
* some random text abp-200 more random text.mkv
> This should still be how it works, I didn't change any of the filename detection, I've only ever tested with ABP-200 type of file naming.

# Development
### Requirements
* Docker
* Docker Compose
* Python
* .NET 9.0

### Building
    $ ./build.sh
    # Visit localhost:8096

### Packaging
    $ python package.py
    # manifest.json will update, and the package will be zipped up in release/

### General
JAV files for testing purposes are stored in videos/

### R18 Database Environment Variables
Set these on the Jellyfin container if you want the plugin to query the PostgreSQL database that contains the imported R18 dump.

* `JELLYFINJAV_R18_DB_CONNECTION_STRING`
* `JELLYFINJAV_R18_DB_HOST`
* `JELLYFINJAV_R18_DB_PORT`
* `JELLYFINJAV_R18_DB_NAME`
* `JELLYFINJAV_R18_DB_USER`
* `JELLYFINJAV_R18_DB_PASSWORD`
* `JELLYFINJAV_R18_DB_SSL_MODE`

If `JELLYFINJAV_R18_DB_CONNECTION_STRING` is not set, the plugin builds a connection string from host, port, database, user, and password. If none of the database environment variables are set, the R18 lookup providers return unavailable results.

# Screenshots
![Grid Example](screenshots/example-grid.jpg)
![Video Example](screenshots/example-video.jpg)
![Actress Example](screenshots/example-actress.jpg)

# License
Licensed under AGPL-3.0-only
