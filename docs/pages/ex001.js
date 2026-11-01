	function testResult() {
		var text = $("#editing").val().trim()
		const regex = /root\s*\.\s*console\s*\.\s*log\(\s*"(.*)"\s*\)(;{0,1})/gi;
		const regexCase = /Root\s*\.\s*Console\s*\.\s*log/g;
		var res = regex.exec(text)
		
		if (res == null) {
			$("#output").html("Type the example better");
			return;
	  }
		var msg = "";
		if (res.length > 1) msg = res[1];
		if (res.length > 2 && res[2]) msg += "\n; is not required";
		if (regexCase.exec(text) == null) msg += "\nRoot and Console need to be written with the first letter uppercase!";
		$("#output").html(msg);
	}