cur_folder_name = ${shell pwd | awk -F'/' '{print $$NF}'}
files = bin/Debug/net10.0/${cur_folder_name}.*
version = ${shell grep -oP '(?<=<Version>)[0-9.]+' permission_manager_api.csproj}
path_deploy = ../../../../shared/${cur_folder_name}

all: deploy
	rm -f *.zip
	rm -f ${cur_folder_name}.dll
	rm -f ${cur_folder_name}.pdb
	rm -f ${cur_folder_name}.deps.json
	dotnet build
	cp ${files} .

# deploys the dll and pdb to the counterstrikesharp addon folder
.PHONY: deploy 
deploy:
	mkdir -p ${path_deploy}
	cp ${cur_folder_name}.* ${path_deploy}/

# creates a zip file of the addon for release
.PHONY: release
release: 
	mkdir -p addons/counterstrikesharp/shared/${cur_folder_name}
	cp ${cur_folder_name}.* addons/counterstrikesharp/shared/${cur_folder_name}/
	zip -r "${cur_folder_name}_${version}.zip" addons/*
