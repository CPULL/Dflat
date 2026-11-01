	function testResult() {
		$("#stdin").css("display", "block");
		initDflatEditor('editing', 'highlighting');
		$("#output").css("display", "none");

		$("#editing").on('keydown', (event) => {
			if (event.key === 'Enter' && !event.shiftKey) {
				event.preventDefault(); // Stop newline from being added

				$("#output").css("display", "block");
				var val = $("#editing").val();
				if ((val+"  ").slice(0, 2) == "> ") val = val.slice(2);
				$("#output").html("Hello, " + val);
				highlightCodeNode($("#output")[0]);
			}
    });

		
	}