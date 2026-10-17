cur_folder_name = ${shell pwd | awk -F'/' '{print $$NF}'}
files = bin/Debug/net10.0/${cur_folder_name}.*
version = ${shell grep -oP '(?<=<Version>)[0-9.]+' permission_manager_api.csproj}

all:
	rm -f *.zip
	rm -f ${cur_folder_name}.dll
	rm -f ${cur_folder_name}.pdb
	rm -f ${cur_folder_name}.deps.json
	dotnet build
	cp ${files} .

.PHONY: release
release:
	mkdir -p addons/counterstrikesharp/shared/${cur_folder_name}
	cp ${cur_folder_name}.* addons/counterstrikesharp/shared/${cur_folder_name}/
	zip -r "${cur_folder_name}_${version}.zip" addons/*

.PHONY: release_config
release_config:
	mkdir -p addons/counterstrikesharp/shared/${cur_folder_name}
	cp ${cur_folder_name}.* addons/counterstrikesharp/shared/${cur_folder_name}/
	zip -r "${cur_folder_name}_${version}.zip" addons/*